using System.Text;

namespace ExportViewer.Core.Tests.Fixtures;

/// <summary>
/// Made-up sensitive values for scanner tests. Numbers are published test values that pass their check digit (for
/// example the ATO's sample ABN and the standard test Visa card). Key-like values are assembled at run time so that no
/// complete key appears in the source and secret scanners don't flag the repository.
/// </summary>
internal static class FakeSecrets
{
    public const string Tfn = "123 456 782";
    public const string TfnUngrouped = "123456782";
    public const string Abn = "51 824 753 556";
    public const string Medicare = "2123 45670 1";
    public const string Card = "4111 1111 1111 1111";
    public const string CardBadChecksum = "4111 1111 1111 1112";
    public const string Iban = "GB82 WEST 1234 5698 7654 32";
    public const string Email = "someone@example.test";
    public const string Mobile = "0412 345 678";
    public const string Ip = "203.0.113.5";

    public static string AwsKey => "AK" + "IA" + "IOSFODNN7" + "EXAMPLE";

    public static string GitHubToken => "gh" + "p_" + new string('a', 18) + new string('B', 18);

    public static string AnthropicKey => "sk-" + "ant-" + "api03-" + new string('x', 12) + "123456789";

    public static string Jwt
    {
        get
        {
            static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"{B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}.{B64("{\"sub\":\"test-user\"}")}.c2lnbmF0dXJlLXRlc3Q";
        }
    }

    public static string PemKey => "-----BEGIN " + "PRIVATE KEY-----\nMIIBVQIBADANBgkqhkiG9w0BAQEFAASCAT8wggE7AgEAAkEA\n-----END " + "PRIVATE KEY-----";
}
