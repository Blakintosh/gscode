namespace GSCode.Parser.AST
{
    internal class ASTTokenReader
    {
        public List<Token> Tokens { get; }
        public int Index { get; set; } = 0;
        private int savedIndex = -1;

        public ASTTokenReader(List<Token> tokens)
        {
            Tokens = tokens;
        }

        public Token ReadAny(int offset = 0)
        {
            if (AtEof(offset))
            {
                return CreateEndOfFileToken();
            }

            return Tokens[Index + offset];
        }

        public Token Read(int offset = 0)
        {
            if(AtEof(offset))
            {
                return CreateEndOfFileToken();
            }

            if (Tokens[Index + offset].Is(TokenType.Comment))
            {
                Index++;
                return Read(offset);
            }
            return Tokens[Index + offset];
        }
        private Token CreateEndOfFileToken()
        {
            Token finalToken = Tokens[Tokens.Count - 1];
            return new Token(finalToken.TextRange, TokenType.Eof, null, "EOF");
        }

        public Token NextAny()
        {
            Token result = ReadAny();
            Index++;
            return result;
        }

        public Token Next()
        {
            Token result = Read();
            Index++;
            return result;
        }

        public Token ReadPreviousAny()
        {
            Token result = ReadAny(-1);
            return result;
        }

        public Token ReadPrevious()
        {
            Token result = Read(-1);
            return result;
        }

        public void SaveCurrentIndex()
        {
            savedIndex = Index;
        }

        public void ReturnToSavedIndex()
        {
            if(savedIndex == -1)
            {
                throw new Exception("ReturnToSavedIndex() called when no saved index was stored.");
            }
            Index = savedIndex;
            savedIndex = -1;
        }

        public bool AtEof(int offset = 0)
        {
            return Index + offset >= Tokens.Count;
        }
    }
}
