

using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
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

namespace GSCode.Data.Structures
{
    public sealed class ScriptTokenLinkedList : TokenLinkedList
    {
        private bool headAndTailAdded = false;

        /// <summary>
        /// Adds the specified element to the linked list.
        /// </summary>
        /// <param name="element">Element to add</param>
        public override void Add(Token element)
        {
            if (headAndTailAdded)
            {
                throw new InvalidOperationException("Unable to use Add() to mutate the token linked list after AddEofNode() has been used.");
            }

            base.Add(element);
        }

        /// <summary>
        /// Adds the final end-of-file node & start-of-file node to the token linked list.
        /// This method removes the ability to use Add() to mutate the linked list in further code, as it is considered a 
        /// start-to-end token representation of a script file.
        /// </summary>
        public void AddBorderNodes()
        {
            headAndTailAdded = true;
            Token? currentLast = Last;

            Token node;
            if (currentLast is null)
            {
                node = new Token(RangeHelper.From(0, 0, 0, 0), TokenType.Eof, null, "EOF")
                {
                    Next = default,
                    Previous = default
                };
                node.Next = node;
                First = node;
                Last = node;
                return;
            }

            SetSofFirst(First! with { Contents = "SOF", Type = TokenType.Sof, SubType = null });
            SetEofLast(currentLast with { Contents = "EOF", Type = TokenType.Eof, SubType = null });
        }

        private void SetSofFirst(Token node)
        {
            First!.Previous = node;
            node.Next = First;
            First = node;
        }

        private void SetEofLast(Token node)
        {
            node.Next = node;
            Last!.Next = node;
            Last = node;
        }
    }

    public class TokenLinkedList
    {
        public Token? First { get; protected set; }
        public Token? Last { get; protected set; }

        /// <summary>
        /// Adds the specified element to the linked list.
        /// </summary>
        /// <param name="element">Element to add</param>
        public virtual void Add(Token element)
        {
            if(First == null || Last == null)
            {
                element.Next = null;
                element.Previous = null;
                First = element;
                Last = element;
                return;
            }

            SetNewLast(element);
        }

        private void SetNewLast(Token newLastToken)
        {
            newLastToken.Previous = Last;
            newLastToken.Next = null;
            Last!.Next = newLastToken;
            Last = newLastToken;
        }

        /// <summary>
        /// Replaces or removes the nodes between the start and end inclusively. The start and end old nodes are assumed to be
        /// part of the linked list (and should be).
        /// </summary>
        /// <param name="startOld">Old start node</param>
        /// <param name="endOld">Old end node</param>
        /// <param name="startNew">New start node. If unspecified, this is a straight removal.</param>
        /// <param name="endNew">New end node. If unspecified, this is a straight removal.</param>
        public void ReplaceRangeInclusive(Token startOld, Token endOld,
            Token? startNew, Token? endNew)
        {
            // TODO: Make TLL safer by not accepting anything other than another TLL as input (forces Next/Prev. relationships)
            // Entire implementation prob. needs a bit of a revision
            if(startNew is null || endNew is null)
            {
                RemoveRange(startOld, endOld);
                return;
            }

            ReplaceStartNode(startOld, startNew);
            ReplaceEndNode(endOld, endNew);
        }

        public void Remove(Token element)
        {
            if(element == First)
            {
                First = element.Next;
                First!.Previous = null;
            }
            if (element == Last)
            {
                Last = element.Previous;
                Last!.Next = null;
            }

            element.Next!.Previous = element.Previous;
            if(element.Previous is not null)
            {
                element.Previous.Next = element.Next;
            }
        }

        private void RemoveRange(Token start, Token end)
        {
            if(end.Next is null && start.Previous is null)
            {
                throw new InvalidOperationException("Attempt to remove an entire token linked list.");
            }

            if(start.Previous is null)
            {
                First = end.Next;
                First!.Previous = null;
                return;
            }
            if(end.Next is null)
            {
                Last = start.Previous;
                Last.Next = null;
                return;
            }

            end.Next.Previous = start.Previous;
            start.Previous.Next = end.Next;
        }

        private void ReplaceEndNode(Token endOld, Token endNew)
        {
            if (endOld.Next is null) // Is the tail
            {
                throw new InvalidOperationException("Attempt to replace the tail node of the token linked list, which is reserved " +
                    "as an end-of-file circular node.");
            }

            Token followingNode = endOld.Next;

            endNew.Next = followingNode;
            followingNode.Previous = endNew;
        }

        private void ReplaceStartNode(Token startOld, Token startNew)
        {
            if (startOld.Previous is null) // Is the head
            {
                First = startNew;
                return;
            }

            Token precedingNode = startOld.Previous;

            startNew.Previous = precedingNode;
            precedingNode.Next = startNew;
        }

        public List<Token> ToList()
        {
            List<Token> output = new();

            Token? token = First;
            while(token != null && !token.IsEof())
            {
                output.Add(token);
                token = token.Next;
            }
            return output;
        }
    }
}
