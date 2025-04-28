using GSCode.Data;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Lexical;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Data;

public sealed class Trie<T>
{
    private readonly TrieNode<T> _root = new(string.Empty);

    sealed record class TrieNode<V>(V value)
    {
        public V Value { get; } = value;
        public Dictionary<char, TrieNode<T>> Children { get; } = new();
    }

    public void Add(string key, T value)
    {
        TrieNode<T> currentNode = _root;

        ReadOnlySpan<char> keySpan = key.AsSpan();

        AddAt(currentNode, keySpan, value);
    }

    private void AddAt(TrieNode<T> node, ReadOnlySpan<char> keySpan, T value)
    {
        if(keySpan.Length == 1)
        {
            node.Children.Add(keySpan[0], new(value));
            return;
        }

        char currentChar = keySpan[0];
        if(!node.Children.TryGetValue(currentChar, out TrieNode<T> child))
        {
            child = new(default!);
            node.Children.Add(currentChar, child);
        }

        AddAt(child, keySpan[1..], value);
    }
}