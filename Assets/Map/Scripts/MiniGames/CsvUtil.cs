using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class CsvUtil : MonoBehaviour
{
    public static Dictionary<string, int> BuildColumnIndex(List<string> header)
    {
        var map = new Dictionary<string, int>();
        for (int i = 0; i < header.Count; i++) map[header[i].Trim()] = i;
        return map;
    }

    /// <summary>
    /// RFC 4180-aware tokenizer. A field containing a comma, a double quote, or a newline
    /// is wrapped in double quotes; a literal double quote inside a quoted field is written
    /// as two double quotes ("") — exactly what Excel/Sheets produce on export. Walking
    /// character-by-character with an "am I inside quotes" flag is what a plain
    /// string.Split(',') cannot do: Split has no concept of "this comma doesn't count."
    /// </summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }  // "" -> "
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',':
                    row.Add(field.ToString()); field.Clear();
                    break;
                case '\r': break;                          // swallow; newline handled on \n
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>();
                    break;
                default: field.Append(c); break;
            }
        }

        if (field.Length > 0 || row.Count > 0)             // final row with no trailing \n
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
