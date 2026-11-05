using Xunit;

namespace Rechunk.Core.Tests.Pii;

public class PiiDetectorTests
{
    private static readonly BuiltInPiiDetector Detector = new();

    // Secrets are built at run time from pieces, so no file in the repository holds something a secret scanner would flag.
    private static string Random(string alphabet, int length, int seed)
    {
        var r = new System.Random(seed);
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[r.Next(alphabet.Length)]).ToArray());
    }

    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string Upper32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static PiiType[] Types(string text) => Detector.Scan(text).Select(f => f.Type).ToArray();

    private static void AssertFound(PiiType type, string text, string value)
    {
        var findings = Detector.Scan(text).Where(f => f.Type == type).ToList();
        var hit = Assert.Single(findings);
        Assert.Equal(value, text.Substring(hit.Start, hit.Length));
    }

    private static void AssertNothing(string text)
    {
        var found = Detector.Scan(text);
        Assert.True(found.Count == 0, "Found " + string.Join(", ", found.Select(f => f.Type + "@" + f.Start)) + " in: " + text);
    }

    // ---- cards ----

    [Theory]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("4111111111111111")]
    [InlineData("5555 5555 5555 4444")]
    [InlineData("3782 822463 10005")]
    [InlineData("6011 1111 1111 1117")]
    public void A_card_number_that_passes_the_check_digit_is_found(string number)
    {
        AssertFound(PiiType.Card, "Please charge " + number + " for the renewal.", number);
    }

    [Theory]
    [InlineData("4111 1111 1111 1112")]
    [InlineData("4111 1111 1111 111")]
    [InlineData("1234 5678 9012 3456")]
    [InlineData("0000 0000 0000 0000")]
    [InlineData("4444 4444 4444 4444")]
    [InlineData("9111 1111 1111 1111")]
    public void Digits_that_look_like_a_card_but_fail_a_check_are_not_found(string number)
    {
        Assert.DoesNotContain(PiiType.Card, Types("Reference " + number + " was logged."));
    }

    [Fact]
    public void A_valid_looking_number_next_to_an_order_word_is_not_a_card()
    {
        AssertNothing("Tracking number 4111111111111111 shipped yesterday.");
        AssertNothing("Order id: 4111 1111 1111 1111 is confirmed.");
    }

    [Fact]
    public void An_order_word_does_not_hide_a_number_that_the_text_calls_a_card()
    {
        Assert.Contains(PiiType.Card, Types("Order paid with card 4111 1111 1111 1111."));
    }

    [Fact]
    public void A_card_number_inside_a_longer_run_of_digits_is_not_found()
    {
        AssertNothing("id 94111111111111111 and 41111111111111112345");
    }

    // ---- Aadhaar ----

    [Fact]
    public void A_grouped_aadhaar_number_with_a_valid_check_digit_is_found()
    {
        AssertFound(PiiType.Aadhaar, "Aadhaar 2345 6789 0124 on file.", "2345 6789 0124");
        AssertFound(PiiType.Aadhaar, "Applicant 2345 6789 0124 submitted.", "2345 6789 0124");
    }

    [Fact]
    public void A_bare_twelve_digit_number_needs_the_word_aadhaar_next_to_it()
    {
        AssertFound(PiiType.Aadhaar, "Aadhaar no 234567890124 verified", "234567890124");
        AssertNothing("Invoice total 234567890124 units");
    }

    [Fact]
    public void An_aadhaar_with_a_wrong_check_digit_or_leading_zero_or_one_is_not_found()
    {
        AssertNothing("Aadhaar 2345 6789 0125 on file.");
        AssertNothing("Aadhaar 0345 6789 0124 on file.");
        AssertNothing("Aadhaar 1345 6789 0124 on file.");
    }

    // ---- PAN, SSN, IBAN ----

    [Fact]
    public void A_pan_with_the_right_shape_is_found()
    {
        AssertFound(PiiType.Pan, "PAN ABCPE1234F belongs to the director.", "ABCPE1234F");
    }

    [Theory]
    [InlineData("ABCDE1234F")]
    [InlineData("ABCPE12345")]
    [InlineData("ABCPE1234FG")]
    [InlineData("abcpe1234f")]
    public void A_pan_with_the_wrong_shape_is_not_found(string text)
    {
        AssertNothing("Code " + text + " here");
    }

    [Fact]
    public void An_ssn_in_the_usual_format_is_found()
    {
        AssertFound(PiiType.Ssn, "SSN 123-45-6789 was given.", "123-45-6789");
    }

    [Theory]
    [InlineData("000-12-3456")]
    [InlineData("666-12-3456")]
    [InlineData("900-12-3456")]
    [InlineData("123-00-6789")]
    [InlineData("123-45-0000")]
    public void An_ssn_the_administration_never_issues_is_not_found(string ssn)
    {
        AssertNothing("Number " + ssn + " was given.");
    }

    [Fact]
    public void Nine_digits_are_an_ssn_only_next_to_the_words_ssn_or_social_security()
    {
        AssertFound(PiiType.Ssn, "Social security number 123456789 on file", "123456789");
        AssertNothing("Shipment 123456789 left the dock");
    }

    [Fact]
    public void An_iban_with_a_valid_checksum_is_found()
    {
        AssertFound(PiiType.Iban, "Pay to GB82 WEST 1234 5698 7654 32 today.", "GB82 WEST 1234 5698 7654 32");
        AssertFound(PiiType.Iban, "IBAN DE89370400440532013000 for the invoice", "DE89370400440532013000");
    }

    [Fact]
    public void Something_shaped_like_an_iban_with_a_bad_checksum_is_not_found()
    {
        AssertNothing("Pay to GB82 WEST 1234 5698 7654 33 today.");
        AssertNothing("Part number AB12CDEF3456GH78 is in stock.");
    }

    // ---- email and phone ----

    [Fact]
    public void An_email_address_is_found()
    {
        AssertFound(PiiType.Email, "Write to jane.doe+news@example.co.uk soon.", "jane.doe+news@example.co.uk");
    }

    [Theory]
    [InlineData("icon@2x.png")]
    [InlineData("user@localhost")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("a..b@example.com")]
    [InlineData("see @mention in the thread")]
    [InlineData("price@5 dollars")]
    public void Things_that_only_look_like_an_email_are_not_found(string text)
    {
        Assert.DoesNotContain(PiiType.Email, Types("Note " + text + " ok"));
    }

    [Theory]
    [InlineData("+91 98765 43210")]
    [InlineData("+1 415 555 2671")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("(415) 555-2671")]
    [InlineData("415-555-2671")]
    [InlineData("98765 43210")]
    public void A_phone_number_in_a_common_format_is_found(string number)
    {
        AssertFound(PiiType.Phone, "Call me on " + number + " tonight.", number);
    }

    [Theory]
    [InlineData("Version 10.2.3.4 shipped")]
    [InlineData("Dated 2026-11-05 and 05/11/2026")]
    [InlineData("Address 192.168.100.200 is private")]
    [InlineData("Build 20261105123456 passed")]
    [InlineData("Coordinates 12.971598, 77.594566")]
    [InlineData("Pages 100-200 of 4567")]
    [InlineData("Part 123-456-7890 is not a phone when it starts with 1")]
    [InlineData("Order 9876543210 is packed")]
    [InlineData("Ticket 98765-43210x")]
    public void Versions_dates_addresses_and_ids_are_not_phone_numbers(string text)
    {
        Assert.DoesNotContain(PiiType.Phone, Types(text));
    }

    [Fact]
    public void A_bare_ten_digit_mobile_number_needs_a_phone_word_next_to_it()
    {
        AssertFound(PiiType.Phone, "Mobile: 9876543210", "9876543210");
        AssertNothing("Reference 9876543210 attached");
    }

    // ---- keys ----

    [Fact]
    public void Keys_with_a_known_prefix_and_a_random_body_are_found()
    {
        var aws = "AKIA" + Random(Upper32, 16, 1);
        var github = "ghp_" + Random(Alnum, 36, 2);
        var stripe = "sk_" + "live_" + Random(Alnum, 24, 3);
        var skProject = "sk-" + "proj-" + Random(Alnum, 40, 4);
        var slack = "xoxb-" + Random("0123456789", 11, 5) + "-" + Random(Alnum, 24, 6);
        var google = "AIza" + Random(Alnum + "_-", 35, 7);
        var npm = "npm_" + Random(Alnum, 36, 8);

        foreach (var key in new[] { aws, github, stripe, skProject, slack, google, npm })
        {
            AssertFound(PiiType.ApiKey, "The key is " + key + " and it works.", key);
        }
    }

    [Fact]
    public void A_json_web_token_and_a_private_key_header_are_found()
    {
        var jwt = "eyJ" + Random(Alnum, 20, 9) + ".eyJ" + Random(Alnum, 30, 10) + "." + Random(Alnum, 43, 11);
        AssertFound(PiiType.ApiKey, "Authorization " + jwt + " sent", jwt);

        var pem = "-----BEGIN " + "RSA PRIVATE KEY-----\n" + Random(Alnum, 64, 12) + "\n-----END " + "RSA PRIVATE KEY-----";
        Assert.Contains(PiiType.ApiKey, Types("Here:\n" + pem + "\nthanks"));
    }

    [Fact]
    public void A_random_value_after_a_key_name_is_found_and_only_the_value_is_marked()
    {
        var value = Random(Alnum, 32, 13);
        var text = "config: api_key = \"" + value + "\"";
        AssertFound(PiiType.ApiKey, text, value);
    }

    [Theory]
    [InlineData("api_key = your_api_key_goes_here")]
    [InlineData("password: changeme-please-now")]
    [InlineData("token = <paste-token-here>")]
    [InlineData("secret: aaaaaaaaaaaaaaaaaaaa")]
    [InlineData("password = administrators")]
    [InlineData("token: 1234567890123456")]
    [InlineData("api_key: example1234567890abcd")]
    public void A_placeholder_or_a_plain_word_after_a_key_name_is_not_a_key(string text)
    {
        AssertNothing(text);
    }

    [Fact]
    public void A_prefix_followed_by_a_repeating_body_is_not_a_key()
    {
        AssertNothing("Use AKIAAAAAAAAAAAAAAAAA as the sample id");
        AssertNothing("ghp_" + new string('a', 36));
    }

    [Fact]
    public void Ordinary_text_with_numbers_hashes_uuids_and_code_has_no_findings()
    {
        AssertNothing("The quarterly report shows revenue grew 12.5% to 4,500,000 while costs fell to 3,100,000.");
        AssertNothing("commit 9fceb02d0ae598e95dc970b74767f19372d61af8 fixed it");
        AssertNothing("id 3f2504e0-4f89-11d3-9a0c-0305e82c3301 was created");
        AssertNothing("sha256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        AssertNothing("var total = items.Sum(i => i.Price * i.Quantity);");
        AssertNothing("See section 4.2.1 on page 1234 and table 5-3.");
    }

    // ---- shape of the answer ----

    [Fact]
    public void Positions_are_utf16_offsets_into_the_text()
    {
        var text = "caf\u00e9 \U0001F600 mail jane@example.org now";
        var hit = Assert.Single(Detector.Scan(text));
        Assert.Equal("jane@example.org", text.Substring(hit.Start, hit.Length));
    }

    [Fact]
    public void A_detector_limited_to_some_types_finds_only_those()
    {
        var only = new BuiltInPiiDetector(new[] { PiiType.Email });
        var found = only.Scan("jane@example.org paid with 4111 1111 1111 1111");
        Assert.Equal(PiiType.Email, Assert.Single(found).Type);
    }

    [Fact]
    public void Strange_input_does_not_hang_or_throw()
    {
        var text = new string('a', 50_000) + "@" + new string('b', 50_000);
        var started = DateTime.UtcNow;
        Detector.Scan(text);
        Detector.Scan(new string('1', 100_000));
        Detector.Scan(string.Concat(Enumerable.Repeat("1 ", 50_000)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("4111111111111112", false)]
    [InlineData("79927398713", true)]
    public void The_luhn_check_matches_known_numbers(string digits, bool valid) => Assert.Equal(valid, PiiChecks.Luhn(digits));

    [Theory]
    [InlineData("234567890124", true)]
    [InlineData("234567890125", false)]
    public void The_verhoeff_check_matches_known_numbers(string digits, bool valid) => Assert.Equal(valid, PiiChecks.Verhoeff(digits));

    [Theory]
    [InlineData("GB82WEST12345698765432", true)]
    [InlineData("GB82WEST12345698765433", false)]
    public void The_iban_check_matches_known_numbers(string compact, bool valid) => Assert.Equal(valid, PiiChecks.Iban(compact));
}
