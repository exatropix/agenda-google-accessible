using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace AgendaAccessible
{
    /// <summary>Petits outils pour lire et écrire du JSON sans dépendance externe.</summary>
    static class Json
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };

        public static Dictionary<string, object> Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return Serializer.DeserializeObject(text) as Dictionary<string, object>;
        }

        public static string Write(object value)
        {
            return Serializer.Serialize(value);
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
            return v is bool && (bool)v;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<Dictionary<string, object>> Arr(Dictionary<string, object> d, string key)
        {
            var list = new List<Dictionary<string, object>>();
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return list;
            var items = v as IEnumerable;
            if (items == null) return list;
            foreach (var item in items)
            {
                var o = item as Dictionary<string, object>;
                if (o != null) list.Add(o);
            }
            return list;
        }
    }
}
