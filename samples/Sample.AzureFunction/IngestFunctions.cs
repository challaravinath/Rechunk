using System.Text.Json;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Rechunk;

namespace Sample.AzureFunction;

/// <summary>What a message on the chunks-in queue looks like.</summary>
public sealed record ChunkMessage(string Tenant, string DocumentId, string[] Chunks);

public sealed class IngestFunctions(ChunkIngestor ingestor, ILogger<IngestFunctions> logger)
{
    /// <summary>
    /// Handles one queue message. The message id is used as the holder, so if the message comes back after a crash
    /// or a timeout, this worker gets its own claims back instead of waiting for the lease to run out.
    ///
    /// What happens to a message:
    ///  - Everything stored or already known: the function returns and the message is deleted.
    ///  - A chunk kept failing: it was written to the dead letter store, its claim is released, and the function still
    ///    returns. The message is not poisoned just because one chunk is bad. Look at the dead letters instead.
    ///  - A chunk is being handled by another worker right now: the function throws, the message becomes visible
    ///    again after the visibility timeout, and the next try will see the finished result.
    ///  - The function keeps throwing: after maxDequeueCount (see host.json) the queue moves the message to
    ///    chunks-in-poison.
    /// </summary>
    [Function("IngestChunks")]
    public async Task Ingest(
        [QueueTrigger("chunks-in")] QueueMessage message,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Deserialize<ChunkMessage>(message.Body.ToString())
            ?? throw new InvalidOperationException("The message is empty.");

        var request = new IngestRequest(
            body.Tenant,
            new DocumentId(body.DocumentId),
            body.Chunks.Select((text, i) => new ChunkInput(i, text)).ToList(),
            Holder: message.MessageId);

        var result = await ingestor.IngestAsync(request, cancellationToken);

        logger.LogInformation(
            "Message {Id} (try {Try}): {Embedded} stored, {Duplicates} duplicates, {InFlight} in flight, {Failed} dead lettered.",
            message.MessageId, message.DequeueCount, result.Embedded, result.Duplicates, result.InFlight, result.DeadLetterCount);

        if (result.InFlight > 0)
        {
            throw new InvalidOperationException(
                $"{result.InFlight} chunks are being handled by another worker, the message will be tried again.");
        }
    }

    /// <summary>
    /// Put any message on the chunks-replay queue to try the dead letters again, for example after the embedding
    /// service is back. The chunk text is not kept in the dead letter store by default, so it is loaded again here.
    /// </summary>
    [Function("ReplayDeadLetters")]
    public async Task Replay(
        [QueueTrigger("chunks-replay")] string trigger,
        CancellationToken cancellationToken)
    {
        var result = await ingestor.ReplayAsync(
            new DeadLetterQuery(Take: 50),
            LoadChunkText,
            cancellationToken);

        logger.LogInformation(
            "Replay: {Attempted} tried, {Succeeded} worked, {Failed} failed again, {Deferred} left for later, {Skipped} without text.",
            result.Attempted, result.Succeeded, result.Failed, result.Deferred, result.SkippedNoText);
    }

    // Look the chunk up in your own source of truth (blob storage, a database, the original document).
    // Returning null leaves the dead letter where it is.
    private static ValueTask<string?> LoadChunkText(DeadLetter deadLetter, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(null);
}
