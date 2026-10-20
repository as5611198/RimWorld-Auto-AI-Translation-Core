using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AutoTranslator_Core
{
    // Contains only bounded, redacted display data. Never persist credentials or raw responses here.
    internal sealed class ApiConnectionDiagnostic
    {
        internal bool Succeeded;
        internal long HttpCode;
        internal string Provider;
        internal string Model;
        internal string Endpoint;
        internal string TransportError;
        internal string ServiceMessage;
        internal string HintKey;

        internal static ApiConnectionDiagnostic Create(string provider, string model, string endpoint,
            bool succeeded, long httpCode, string transportError, string responseBody, params string[] secrets)
        {
            Uri uri;
            string safeEndpoint = Uri.TryCreate(endpoint, UriKind.Absolute, out uri)
                ? uri.GetLeftPart(UriPartial.Path) : string.Empty;
            // User info and query strings (including Google's key parameter) must not enter a report.
            if (uri != null && !string.IsNullOrEmpty(uri.UserInfo)) safeEndpoint = string.Empty;
            string error = Redact(transportError, secrets);
            return new ApiConnectionDiagnostic
            {
                Succeeded = succeeded,
                HttpCode = httpCode,
                Provider = Redact(provider, secrets),
                Model = Redact(model, secrets),
                Endpoint = Redact(safeEndpoint, secrets),
                TransportError = error,
                ServiceMessage = succeeded ? string.Empty : ExtractServiceMessage(responseBody, secrets),
                HintKey = GetHintKey(succeeded, httpCode, error)
            };
        }

        internal ApiConnectionDiagnostic TimedOut()
        {
            return new ApiConnectionDiagnostic
            {
                Provider = Provider, Model = Model, Endpoint = Endpoint,
                HintKey = "ATC_ConnectionTest_Timeout", TransportError = string.Empty,
                ServiceMessage = string.Empty
            };
        }

        internal string GetNotificationDetail()
        {
            if (Succeeded) return string.Empty;
            string detail = string.IsNullOrWhiteSpace(ServiceMessage) ? TransportError : ServiceMessage;
            detail = Regex.Replace(detail ?? string.Empty, @"\s+", " ").Trim();
            const int limit = 180;
            if (detail.Length <= limit) return detail;
            int length = char.IsHighSurrogate(detail[limit - 1]) ? limit - 1 : limit;
            return detail.Substring(0, length) + "…";
        }

        private static string GetHintKey(bool success, long code, string error)
        {
            if (success) return "ATC_ConnectionTest_Success";
            if (code == 401) return "ATC_ConnectionTest_Unauthorized";
            if (code == 403) return "ATC_ConnectionTest_Forbidden";
            if (code == 404) return "ATC_ConnectionTest_NotFound";
            if (code == 429) return "ATC_ConnectionTest_RateLimit";
            if (code >= 500) return "ATC_ConnectionTest_ServerError";
            if (code >= 400) return "ATC_ConnectionTest_Rejected";
            if (error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ATC_ConnectionTest_Timeout";
            return "ATC_ConnectionTest_NoResponse";
        }

        private static string ExtractServiceMessage(string body, string[] secrets)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            string trimmed = body.Trim();
            if (trimmed.StartsWith("<", StringComparison.Ordinal))
                return string.Empty; // Do not display gateway HTML, scripts, or a login page as an API error.
            try
            {
                JToken token = JToken.Parse(body);
                foreach (string path in new[] { "error.message", "message", "detail", "error.description", "error", "error.code", "error.type" })
                {
                    JToken value = token.SelectToken(path, false);
                    if (value is JValue && value.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)value))
                        return Redact((string)value, secrets);
                }
                return string.Empty; // Do not expose arbitrary JSON fields that might echo request credentials.
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return Redact(body, secrets);
            }
        }

        internal static string Redact(string value, params string[] secrets)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            // Keep a generous lookahead past the display limit so a boundary cannot expose a partial key.
            string text = Decode(value.Length > 65536 ? value.Substring(0, 65536) : value);
            foreach (string secret in (secrets ?? new string[0]).Where(s => !string.IsNullOrEmpty(s))
                .SelectMany(s => new[] { s, Decode(s) }).Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length))
                text = text.Replace(secret, "[redacted]");
            text = Regex.Replace(text, @"\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=\-]+", "[redacted]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text,
                @"([""']?(?:authorization|x-api-key|api[_-]?key|key|access[_-]?token|refresh[_-]?token|token|secret|password|player2-game-key)[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s&,;}\]]+)",
                "$1[redacted]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(?:sk-|AIza)[A-Za-z0-9_\-]{6,}", "[redacted]");
            text = Regex.Replace(text, @"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", "[redacted]");
            text = Regex.Replace(text, "<[^>]*>", string.Empty);
            text = new string(text.Where(c => !char.IsControl(c) || c == '\n' || c == '\t').ToArray()).Trim();
            return text.Length > 1500 ? text.Substring(0, 1500) + "…" : text;
        }

        private static string Decode(string value)
        {
            string text = WebUtility.HtmlDecode(value);
            text = Regex.Replace(text, @"\\u([0-9a-fA-F]{4})", m =>
                ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            try { text = Uri.UnescapeDataString(text); }
            catch (UriFormatException) { }
            return text.Replace("\\/", "/");
        }
    }
}
