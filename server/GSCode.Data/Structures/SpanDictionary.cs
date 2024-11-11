/**
	GSCode.NET Language Server
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System.Text;

namespace GSCode.Data.Structures
{
    public sealed class SpanDictionary
    {
        private readonly List<Dictionary<char, bool>> internalDicts = new();

        public void Add(string key)
        {
            for(int i = 0; i < key.Length; i++)
            {
                if(i == internalDicts.Count)
                {
                    internalDicts.Add(new()
                    {
                        { key[i], true }
                    });
                }
                else if (!internalDicts[i].ContainsKey(key[i]))
                {
                    internalDicts[i].Add(key[i], true);
                }
            }
        }

        public string? FindMatch(ReadOnlySpan<char> charSpan, int currentIndex)
        {
            if (!internalDicts[0].ContainsKey(charSpan[currentIndex]))
            {
                return null;
            }

            StringBuilder builder = new();
            builder.Append(charSpan[currentIndex]);

            for(int i = 1; i < internalDicts.Count; i++)
            {
                if (i + currentIndex == charSpan.Length || !internalDicts[i].ContainsKey(charSpan[i + currentIndex]))
                {
                    return builder.ToString();
                }
                builder.Append(charSpan[i + currentIndex]);
            }
            return builder.ToString();
        }
    }
}
