using System;

namespace AutoTranslator_Core
{
    // Validate before any request or DNS lookup. Errors must never contain the pasted value.
    internal static class ApiEndpointRules
    {
        internal const string InvalidUrl = "ATC_Error_InvalidApiBaseUrl";
        internal const string KeyInUrl = "ATC_Error_ApiKeyInUrl";

        internal static bool TryNormalize(string input, string apiKey, out string baseUrl, out string errorKey)
        {
            baseUrl = string.Empty;
            errorKey = string.Empty;
            string value = (input ?? string.Empty).Trim();
            if (value.Length == 0) return true;

            string key = (apiKey ?? string.Empty).Trim();
            if (LooksLikeKey(value) ||
                (key.Length > 0 && string.Equals(value, key, StringComparison.Ordinal)) ||
                (key.Length >= 8 && value.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                errorKey = KeyInUrl;
                return false;
            }

            // Do not turn arbitrary text (including an old key) into a network address.
            Uri uri;
            if ((!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) || value.IndexOf('\\') >= 0)
            {
                errorKey = InvalidUrl;
                return false;
            }

            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    errorKey = InvalidUrl;
                    return false;
                }
            }

            string decoded = Uri.UnescapeDataString(uri.AbsoluteUri);
            if ((uri.Host.IndexOf('.') < 0 && LooksLikeKey(uri.Host)) ||
                (key.Length >= 8 && decoded.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                errorKey = KeyInUrl;
                return false;
            }

            baseUrl = uri.AbsoluteUri.TrimEnd('/');
            foreach (string suffix in new[] { "/chat/completions", "/models" })
            {
                if (!baseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                baseUrl = baseUrl.Substring(0, baseUrl.Length - suffix.Length);
                break;
            }
            return true;
        }

        private static bool LooksLikeKey(string value)
        {
            return value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("AIza", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                   (value.StartsWith("eyJ", StringComparison.Ordinal) && value.Length > 40);
        }
    }
}
