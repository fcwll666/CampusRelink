using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;

static class PortalProtocol
{
    internal static string BuildPayload(PortalSettings settings, CredentialAccount account)
    {
        if (settings == null) throw new ArgumentNullException("settings");
        string error = settings.Validate();
        if (error != null) throw new ArgumentException(error, "settings");
        if (account == null || !account.IsComplete) throw new ArgumentException("请先配置完整的账户和密码。", "account");
        Dictionary<string, string> fields;
        error = settings.TryExtraFields(out fields);
        if (error != null) throw new ArgumentException(error, "settings");
        fields.Add(settings.UsernameField, account.Username);
        fields.Add(settings.PasswordField, account.Password);
        if (settings.RequestEncoding == "Json") return new JavaScriptSerializer().Serialize(fields);
        StringBuilder form = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in fields)
        {
            if (form.Length != 0) form.Append('&');
            form.Append(HttpUtility.UrlEncode(pair.Key, Encoding.UTF8));
            form.Append('=');
            form.Append(HttpUtility.UrlEncode(pair.Value, Encoding.UTF8));
        }
        return form.ToString();
    }

    internal static string ContentType(PortalSettings settings)
    {
        if (settings == null) throw new ArgumentNullException("settings");
        if (settings.RequestEncoding == "Json") return "application/json; charset=utf-8";
        if (settings.RequestEncoding == "Form") return "application/x-www-form-urlencoded; charset=utf-8";
        throw new ArgumentException("请求编码只能选择 Json 或 Form。", "settings");
    }

    internal static bool IsSuccess(PortalSettings settings, string response)
    {
        bool success;
        return TryReadSuccess(settings, response, out success) && success;
    }

    internal static bool TryReadSuccess(PortalSettings settings, string response, out bool success)
    {
        success = false;
        if (settings == null || String.IsNullOrWhiteSpace(settings.SuccessField) || String.IsNullOrWhiteSpace(response)) return false;
        try
        {
            // JavaScriptSerializer also accepts JavaScript extensions (unquoted
            // keys, single quotes). Check JSON grammar before interpreting it.
            if (!new StrictJsonSyntax(response).IsValid()) return false;
            object value = new JavaScriptSerializer().DeserializeObject(response);
            foreach (string part in settings.SuccessField.Split('.'))
            {
                Dictionary<string, object> properties = value as Dictionary<string, object>;
                if (String.IsNullOrWhiteSpace(part) || properties == null || !properties.TryGetValue(part, out value)) return false;
            }
            string scalar;
            if (value is string) scalar = (string)value;
            else if (value is bool) scalar = (bool)value ? "true" : "false";
            else if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal) scalar = Convert.ToString(value, CultureInfo.InvariantCulture);
            else return false;
            success = String.Equals(scalar, settings.SuccessValue, StringComparison.Ordinal);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (OverflowException) { return false; }
    }
}

sealed class StrictJsonSyntax
{
    readonly string text;
    int position;
    internal StrictJsonSyntax(string value) { text = value; }

    internal bool IsValid()
    {
        if (text.Length > 2097152) return false;
        SkipSpace();
        if (!Value(0)) return false;
        SkipSpace();
        return position == text.Length;
    }
    void SkipSpace()
    {
        while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n')) position++;
    }
    bool Take(char expected)
    {
        SkipSpace();
        if (position == text.Length || text[position] != expected) return false;
        position++;
        return true;
    }
    bool Value(int depth)
    {
        if (depth > 100) return false;
        SkipSpace();
        if (position == text.Length) return false;
        char token = text[position];
        if (token == '"') return QuotedString();
        if (token == '{') return ObjectValue(depth + 1);
        if (token == '[') return ArrayValue(depth + 1);
        if (token == 't') return Literal("true");
        if (token == 'f') return Literal("false");
        if (token == 'n') return Literal("null");
        return Number();
    }
    bool ObjectValue(int depth)
    {
        position++;
        if (Take('}')) return true;
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            SkipSpace();
            int start = position;
            if (!QuotedString()) return false;
            string key = new JavaScriptSerializer().Deserialize<string>(text.Substring(start, position - start));
            if (!keys.Add(key) || !Take(':') || !Value(depth)) return false;
            if (Take('}')) return true;
            if (!Take(',')) return false;
        }
    }
    bool ArrayValue(int depth)
    {
        position++;
        if (Take(']')) return true;
        while (true)
        {
            if (!Value(depth)) return false;
            if (Take(']')) return true;
            if (!Take(',')) return false;
        }
    }
    bool QuotedString()
    {
        if (position == text.Length || text[position++] != '"') return false;
        while (position < text.Length)
        {
            char current = text[position++];
            if (current == '"') return true;
            if (current < 0x20) return false;
            if (current != '\\') continue;
            if (position == text.Length) return false;
            char escaped = text[position++];
            if (escaped == 'u')
            {
                for (int digit = 0; digit < 4; digit++)
                {
                    if (position == text.Length) return false;
                    char hex = text[position++];
                    if (!((hex >= '0' && hex <= '9') || (hex >= 'a' && hex <= 'f') || (hex >= 'A' && hex <= 'F'))) return false;
                }
            }
            else if (escaped != '"' && escaped != '\\' && escaped != '/' && escaped != 'b' && escaped != 'f' && escaped != 'n' && escaped != 'r' && escaped != 't') return false;
        }
        return false;
    }
    bool Literal(string value)
    {
        if (text.Length - position < value.Length || String.CompareOrdinal(text, position, value, 0, value.Length) != 0) return false;
        position += value.Length;
        return true;
    }
    bool Digit() { return position < text.Length && text[position] >= '0' && text[position] <= '9'; }
    bool Number()
    {
        if (text[position] == '-') position++;
        if (!Digit()) return false;
        if (text[position] == '0') position++;
        else while (Digit()) position++;
        if (position < text.Length && text[position] == '.')
        {
            position++;
            if (!Digit()) return false;
            while (Digit()) position++;
        }
        if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
        {
            position++;
            if (position < text.Length && (text[position] == '+' || text[position] == '-')) position++;
            if (!Digit()) return false;
            while (Digit()) position++;
        }
        return true;
    }
}
