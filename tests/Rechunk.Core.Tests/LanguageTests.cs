namespace Rechunk.Core.Tests;

/// <summary>
/// Language tags and the scripts without spaces. The texts are made from character pools with a fixed seed: they are not
/// real sentences, but they have the same shape for the tokenizer (long runs of one script) and they stay the same on
/// every run. The edits are small on purpose, and the seeds were checked so the numbers stay well inside the limits.
/// </summary>
public class LanguageTests
{
    private const string HanPool =
        "我们的公司在这个城市里有很多年轻人他们每天都会去工作学习生活和朋友一起吃饭聊天看书运动旅行音乐电影时间世界国家问题发展经济社会文化教育科学技术研究";

    private const string KanaPool = "あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわをんがぎぐげござじずぜぞだでどばびぶべぼ";

    private const string KanjiPool = "日本語今朝会社仕事時間学校勉強友達食事天気電車駅会議資料報告確認予定変更連絡電話番号";

    private const string ThaiPool = "กขคงจชซดตถทธนบปผพฟมยรลวศษสหอฮะาิีึืุูเแโใไ";

    private static string Run(string pool, int seed, int length)
    {
        var random = new Random(seed);
        return new string(Enumerable.Range(0, length).Select(_ => pool[random.Next(pool.Length)]).ToArray());
    }

    private static string Chinese(int seed, int length) => Run(HanPool, seed, length);

    private static string Japanese(int seed, int length)
    {
        // kanji and kana mixed, the way real Japanese reads
        var random = new Random(seed);
        var chars = Enumerable.Range(0, length)
            .Select(_ => random.Next(3) == 0 ? KanjiPool[random.Next(KanjiPool.Length)] : KanaPool[random.Next(KanaPool.Length)]);
        return new string(chars.ToArray());
    }

    private static string Thai(int seed, int length) => Run(ThaiPool, seed, length);

    private static string Replace(string text, params int[] positions)
    {
        var chars = text.ToCharArray();
        foreach (var p in positions)
        {
            // a letter from the same script that is not already there, so the text really changes
            chars[p] = chars[p] == 'x' ? 'y' : 'x';
        }

        return new string(chars);
    }

    private static RechunkPipeline Pipeline(Action<RechunkOptions>? configure = null) =>
        RechunkPipeline.CreateBuilder().UseSimHash().UseMinHash().Configure(o => configure?.Invoke(o)).Build();

    private static List<ChunkInput> Two(string a, string b, string? languageA = null, string? languageB = null) =>
        new() { new ChunkInput(0, a, languageA), new ChunkInput(1, b, languageB) };

    // ---- tags ----

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en-US", "en")]
    [InlineData("en_GB", "en")]
    [InlineData(" fr ", "fr")]
    [InlineData("zh-Hant-TW", "zh")]
    public void A_tag_is_cut_to_its_primary_language(string tag, string expected) =>
        Assert.Equal(expected, LanguageTags.Normalize(tag));

    [Fact]
    public void The_region_can_be_kept_when_it_matters()
    {
        Assert.Equal("zh-hant", LanguageTags.Normalize("zh-Hant", primaryOnly: false));
        Assert.Equal("pt-br", LanguageTags.Normalize("pt_BR", primaryOnly: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("und")]
    public void An_empty_or_undetermined_tag_means_untagged(string? tag) =>
        Assert.Null(LanguageTags.Normalize(tag));

    [Theory]
    [InlineData("english!")]
    [InlineData("en us")]
    [InlineData("fr/ca")]
    public void A_tag_that_is_not_a_code_is_rejected(string tag) =>
        Assert.Throws<ArgumentException>(() => LanguageTags.Normalize(tag));

    [Fact]
    public void An_untagged_scope_stays_as_it_was_and_a_tagged_one_is_kept_apart()
    {
        Assert.Equal("docs", LanguageTags.ScopeFor("docs", null));
        Assert.NotEqual(LanguageTags.ScopeFor("docs", "en"), LanguageTags.ScopeFor("docs", "fr"));
        Assert.NotEqual("docs", LanguageTags.ScopeFor("docs", "en"));
    }

    [Fact]
    public void A_scope_cannot_be_confused_with_another_scope_plus_a_language()
    {
        // a scope that happens to end in the language of another one stays different
        Assert.NotEqual(LanguageTags.ScopeFor("docs", "en"), LanguageTags.ScopeFor("docs.en", null));
        Assert.NotEqual(LanguageTags.ScopeFor("docs", "en"), LanguageTags.ScopeFor("docs-en", null));
    }

    [Fact]
    public void An_id_without_a_language_is_the_plain_hex_hash_as_before()
    {
        var hash = new byte[] { 0x01, 0xAB, 0xFF };
        Assert.Equal("01abff", LanguageTags.IdFor(hash, null).Value);
        Assert.Equal("01abff.fr", LanguageTags.IdFor(hash, "fr").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("en")]
    [InlineData("zh")]
    public void An_id_can_be_taken_apart_again(string? language)
    {
        var hash = new byte[] { 1, 2, 3, 4 };
        var id = LanguageTags.IdFor(hash, language);

        Assert.True(LanguageTags.TryParseId(id, out var parsedHash, out var parsedLanguage));
        Assert.Equal(hash, parsedHash);
        Assert.Equal(language, parsedLanguage);
        Assert.Equal(language, LanguageTags.FromId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("abc")]
    [InlineData(".en")]
    public void Something_that_is_not_an_id_does_not_parse(string value)
    {
        Assert.False(LanguageTags.TryParseId(new ChunkId(value), out _, out _));
        Assert.Null(LanguageTags.FromId(new ChunkId(value)));
    }

    // ---- the built-in detector ----

    [Theory]
    [InlineData("The results of the study are not clear, and this is what we have from the lab for now.", "en")]
    [InlineData("Les résultats de cette étude ne sont pas clairs et nous avons besoin de plus de temps pour le dire.", "fr")]
    [InlineData("Die Ergebnisse der Studie sind nicht klar und wir haben mehr Zeit für die Arbeit auf dem Feld.", "de")]
    [InlineData("Los resultados de la investigación no son claros y necesitamos más tiempo para el trabajo.", "es")]
    public void Latin_text_is_told_apart_by_common_words(string text, string expected) =>
        Assert.Equal(expected, ScriptLanguageDetector.Default.Detect(text));

    [Theory]
    [InlineData("안녕하세요 오늘은 날씨가 아주 좋습니다", "ko")]
    [InlineData("สวัสดีครับวันนี้อากาศดีมากเลยนะครับ", "th")]
    [InlineData("Привет, сегодня очень хорошая погода в городе", "ru")]
    [InlineData("Σήμερα ο καιρός είναι πολύ καλός στην πόλη", "el")]
    [InlineData("היום מזג האוויר טוב מאוד בעיר", "he")]
    [InlineData("اليوم الطقس جميل جدا في المدينة", "ar")]
    [InlineData("आज शहर में मौसम बहुत अच्छा है", "hi")]
    public void A_script_that_belongs_to_one_language_gives_that_language(string text, string expected) =>
        Assert.Equal(expected, ScriptLanguageDetector.Default.Detect(text));

    [Fact]
    public void Han_alone_is_Chinese_and_Han_with_kana_is_Japanese()
    {
        Assert.Equal("zh", ScriptLanguageDetector.Default.Detect(Chinese(1, 80)));
        Assert.Equal("ja", ScriptLanguageDetector.Default.Detect(Japanese(1, 80)));
        Assert.Equal("ja", ScriptLanguageDetector.Default.Detect("今日は天気がいいですね。会社に行きます。"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345 67890 !!! ???")]
    [InlineData("Lorem ipsum dolor sit amet")]
    public void Text_without_enough_evidence_gives_no_language(string text) =>
        Assert.Null(ScriptLanguageDetector.Default.Detect(text));

    [Fact]
    public void Mixed_text_takes_the_script_that_has_most_of_the_letters()
    {
        var mostlyChinese = Chinese(2, 120) + " ok";
        var mostlyEnglish = "The report is ready and the team will share it with you in the morning, as we said. " + Chinese(3, 6);

        Assert.Equal("zh", ScriptLanguageDetector.Default.Detect(mostlyChinese));
        Assert.Equal("en", ScriptLanguageDetector.Default.Detect(mostlyEnglish));
    }

    // ---- how a chunk gets its language ----

    [Fact]
    public void Chunks_are_untagged_unless_the_caller_or_a_detector_says_otherwise()
    {
        var pipeline = Pipeline();
        Assert.Null(pipeline.LanguageFor(new ChunkInput(0, Chinese(4, 80))));
        Assert.Equal("fr", pipeline.LanguageFor(new ChunkInput(0, "hello", "FR")));
    }

    [Fact]
    public void Auto_detect_fills_in_the_language_of_untagged_chunks_and_a_tag_wins_over_it()
    {
        var pipeline = Pipeline(o => o.Language.AutoDetect = true);

        Assert.Equal("zh", pipeline.LanguageFor(new ChunkInput(0, Chinese(5, 80))));
        Assert.Equal("th", pipeline.LanguageFor(new ChunkInput(0, Chinese(5, 80), "th")));
    }

    private sealed class FixedDetector(string? answer) : ILanguageDetector
    {
        public int Calls { get; private set; }

        public string? Detect(ReadOnlySpan<char> text)
        {
            Calls++;
            return answer;
        }
    }

    [Fact]
    public void A_custom_detector_is_used_only_for_chunks_without_a_tag()
    {
        var detector = new FixedDetector("de-AT");
        var pipeline = Pipeline(o => o.Language.Detector = detector);

        Assert.Equal("de", pipeline.LanguageFor(new ChunkInput(0, "anything")));
        Assert.Equal("fr", pipeline.LanguageFor(new ChunkInput(1, "anything", "fr")));
        Assert.Equal(1, detector.Calls);
    }

    [Fact]
    public void The_id_carries_the_language_and_an_untagged_chunk_keeps_the_plain_id()
    {
        var pipeline = Pipeline();
        var text = Chinese(6, 60);

        var plain = pipeline.IdFor(new ChunkInput(0, text));
        var tagged = pipeline.IdFor(new ChunkInput(0, text, "zh"));

        Assert.Equal(pipeline.IdFor(text), plain);
        Assert.Equal(plain.Value + ".zh", tagged.Value);
    }

    [Fact]
    public void Region_variants_are_one_language_unless_the_options_say_otherwise()
    {
        var text = Chinese(7, 60);
        var merged = Pipeline();
        var split = Pipeline(o => o.Language.PrimarySubtagOnly = false);

        Assert.Equal(merged.IdFor(new ChunkInput(0, text, "zh-Hans")), merged.IdFor(new ChunkInput(0, text, "zh-Hant")));
        Assert.NotEqual(split.IdFor(new ChunkInput(0, text, "zh-Hans")), split.IdFor(new ChunkInput(0, text, "zh-Hant")));
    }

    // ---- different languages are never duplicates ----

    [Fact]
    public async Task The_same_text_in_two_languages_is_not_an_exact_duplicate()
    {
        var text = TestText.Paragraph(11, 120);
        var report = await Pipeline().DeduplicateAsync("docs", Two(text, text, "en", "fr"));

        Assert.All(report.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
        Assert.NotEqual(report.Decisions[0].Id, report.Decisions[1].Id);
        Assert.Equal(new[] { "en", "fr" }, report.Decisions.Select(d => d.Language));
    }

    [Fact]
    public async Task A_near_copy_in_another_language_is_not_a_near_duplicate_for_either_stage()
    {
        var words = TestText.Words(12, 300);
        var edited = (string[])words.Clone();
        edited[100] = "zqbb";
        var pipeline = Pipeline();

        var other = await pipeline.DeduplicateAsync("docs", Two(string.Join(' ', words), string.Join(' ', edited), "en", "fr"));
        var same = await Pipeline().DeduplicateAsync("docs", Two(string.Join(' ', words), string.Join(' ', edited), "en", "en"));

        Assert.All(other.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
        Assert.Equal(DedupOutcome.Duplicate, same.Decisions[1].Outcome);
    }

    [Fact]
    public async Task The_same_text_in_the_same_language_is_still_a_duplicate()
    {
        var text = TestText.Paragraph(13, 120);
        var report = await Pipeline().DeduplicateAsync("docs", Two(text, text, "en", "en-GB"));

        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
        Assert.Equal(MatchStage.Exact, report.Decisions[1].Stage);
    }

    [Fact]
    public async Task A_tagged_chunk_and_an_untagged_one_are_kept_apart()
    {
        var text = TestText.Paragraph(14, 120);
        var report = await Pipeline().DeduplicateAsync("docs", Two(text, text, "en", null));

        Assert.All(report.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
    }

    [Fact]
    public async Task Languages_are_kept_apart_across_calls_when_the_store_remembers()
    {
        var store = new InMemoryFingerprintStore();
        var pipeline = RechunkPipeline.CreateBuilder().UseSimHash().UseMinHash().UseFingerprintStore(store).Build();
        var words = TestText.Words(15, 300);
        var text = string.Join(' ', words);

        var first = await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, text, "en") });
        await store.CommitAsync(first.Claims.Single().ToCommit());
        var other = await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, text, "fr") });
        var again = await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, text, "en") });

        Assert.Equal(DedupOutcome.Unique, other.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Duplicate, again.Decisions[0].Outcome);
    }

    [Fact]
    public async Task With_auto_detect_the_same_words_in_two_scripts_are_two_chunks()
    {
        var pipeline = Pipeline(o => o.Language.AutoDetect = true);
        var report = await pipeline.DeduplicateAsync("docs", Two(Chinese(16, 120), Thai(16, 120)));

        Assert.All(report.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
        Assert.Equal(new[] { "zh", "th" }, report.Decisions.Select(d => d.Language));
    }

    // ---- near duplicates in scripts without spaces ----

    public static TheoryData<string> Scripts => new() { "zh", "ja", "th" };

    private static string Make(string script, int seed, int length) => script switch
    {
        "zh" => Chinese(seed, length),
        "ja" => Japanese(seed, length),
        _ => Thai(seed, length),
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task A_small_edit_in_a_long_text_is_caught_as_a_near_duplicate(string script)
    {
        var text = Make(script, 21, 600);
        var report = await Pipeline().DeduplicateAsync("docs", Two(text, Replace(text, 300), script, script));

        var second = report.Decisions[1];
        Assert.Equal(DedupOutcome.Duplicate, second.Outcome);
        Assert.NotEqual(MatchStage.Exact, second.Stage);
        Assert.Equal(report.Decisions[0].Id, second.DuplicateOf);
        Assert.NotNull(second.EstimatedJaccard);
        Assert.True(second.EstimatedJaccard >= 0.9, $"estimate was {second.EstimatedJaccard}");
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task The_same_catch_works_without_a_tag_when_detection_is_on(string script)
    {
        var text = Make(script, 22, 600);
        var report = await Pipeline(o => o.Language.AutoDetect = true)
            .DeduplicateAsync("docs", Two(text, Replace(text, 300)));

        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
        Assert.Equal(script, report.Decisions[1].Language);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task Two_different_texts_in_the_same_script_are_not_duplicates(string script)
    {
        var report = await Pipeline().DeduplicateAsync(
            "docs", Two(Make(script, 23, 600), Make(script, 24, 600), script, script));

        Assert.All(report.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("ja")]
    public async Task Without_character_shingles_a_run_of_text_is_one_word_and_the_edit_is_not_caught(string script)
    {
        // this is the problem the character shingles solve: the whole run is a single token, so any edit changes all of it
        var text = Make(script, 25, 600);
        var report = await Pipeline(o => o.Language.CharacterShingles = false)
            .DeduplicateAsync("docs", Two(text, Replace(text, 300), script, script));

        Assert.All(report.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task A_text_that_is_too_short_for_the_fuzzy_stages_only_matches_exactly(string script)
    {
        var text = Make(script, 26, 10);
        var report = await Pipeline().DeduplicateAsync(
            "docs", new List<ChunkInput>
            {
                new(0, text, script), new(1, Replace(text, 5), script), new(2, text, script),
            });

        Assert.Equal(DedupOutcome.Unique, report.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Unique, report.Decisions[1].Outcome);
        Assert.Equal(MatchStage.Exact, report.Decisions[2].Stage);
    }

    [Fact]
    public async Task The_shingle_size_can_be_changed()
    {
        var text = Chinese(27, 600);
        var report = await Pipeline(o => o.Language.CharShingleSize = 2)
            .DeduplicateAsync("docs", Two(text, Replace(text, 300), "zh", "zh"));

        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
    }

    [Fact]
    public void Bad_language_options_are_refused_when_the_pipeline_is_built()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Pipeline(o => o.Language.CharShingleSize = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pipeline(o => o.Language.CharsPerWord = 0));
    }

    // ---- mixed scripts ----

    [Fact]
    public async Task Chinese_inside_an_english_paragraph_is_matched_by_both_parts()
    {
        var english = TestText.Words(31, 150);
        var chinese = Chinese(31, 300);
        var text = string.Join(' ', english) + " " + chinese;

        var editedChinese = Replace(text, text.Length - 100);
        var editedEnglish = string.Join(' ', english.Select((w, i) => i == 60 ? "zqbb" : w)) + " " + chinese;
        var report = await Pipeline().DeduplicateAsync(
            "docs", new List<ChunkInput> { new(0, text, "en"), new(1, editedChinese, "en"), new(2, editedEnglish, "en") });

        Assert.Equal(DedupOutcome.Unique, report.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[2].Outcome);
    }

    [Fact]
    public async Task A_chunk_of_one_script_does_not_match_a_chunk_of_another_even_when_tagged_the_same()
    {
        var report = await Pipeline().DeduplicateAsync(
            "docs", Two(Chinese(32, 600), TestText.Paragraph(32, 150), "und", "und"));

        Assert.All(report.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
    }

    [Fact]
    public async Task Japanese_and_Chinese_with_the_same_characters_are_kept_apart_when_tagged()
    {
        var text = Chinese(33, 600);
        var report = await Pipeline().DeduplicateAsync("docs", Two(text, text, "zh", "ja"));

        Assert.All(report.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
    }

    [Fact]
    public async Task Punctuation_and_spaces_between_characters_do_not_hide_a_duplicate()
    {
        var text = Chinese(34, 600);
        var spaced = string.Join("，", Enumerable.Range(0, 30).Select(i => text.Substring(i * 20, 20)));
        var plain = string.Join("", Enumerable.Range(0, 30).Select(i => text.Substring(i * 20, 20)));

        var report = await Pipeline().DeduplicateAsync("docs", Two(plain, spaced, "zh", "zh"));

        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
    }

    [Theory]
    [InlineData("三十", "六十")]
    [InlineData("有", "没有")]
    public async Task The_guard_keeps_chinese_chunks_apart_when_a_number_or_a_negation_differs(string before, string after)
    {
        var head = Chinese(35, 300);
        var tail = Chinese(36, 300);
        var report = await Pipeline().DeduplicateAsync(
            "docs", Two(head + "员工每年" + before + "天带薪假期" + tail, head + "员工每年" + after + "天带薪假期" + tail, "zh", "zh"));

        Assert.All(report.Decisions, d => Assert.Equal(DedupOutcome.Unique, d.Outcome));
    }

    [Fact]
    public async Task The_same_chinese_chunks_without_the_difference_are_merged()
    {
        var head = Chinese(35, 300);
        var tail = Chinese(36, 300);
        var report = await Pipeline().DeduplicateAsync(
            "docs", Two(head + "员工每年三十天带薪假期" + tail, head + "员工每年三十天带薪假期" + Replace(tail, 150), "zh", "zh"));

        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
    }

    // ---- the latin side stays as it was ----

    [Fact]
    public async Task Text_with_words_and_no_tags_behaves_exactly_as_before()
    {
        var words = TestText.Words(41, 300);
        var edited = (string[])words.Clone();
        edited[100] = "zqbb";

        var report = await Pipeline().DeduplicateAsync(
            "docs", Two(string.Join(' ', words), string.Join(' ', edited)));

        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
        Assert.All(report.Decisions, d => Assert.Null(d.Language));
        Assert.DoesNotContain('.', report.Decisions[0].Id.Value);
    }
}
