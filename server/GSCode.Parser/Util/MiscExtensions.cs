using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Util;

internal static class DictionaryExtensions
{
    public static V Pop<K, V>(this Dictionary<K, V> dictionary, K key)
    {
        V value = dictionary[key];
        dictionary.Remove(key);

        return value;
    }
}
