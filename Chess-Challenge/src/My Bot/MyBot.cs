using ChessChallenge.API;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

public class MyBot : IChessBot
{
    //Value of pieces for taking and protecting - None, Pawn, Knight, Bishop, Rook, King, Queen.
    static public int[] pieceValues = { 0, 110, 150, 200, 200, 400, 800 };

    //Cost of moving a piece, to discourage moving high value pieces - None, Pawn, Knight, Bishop, Rook, King, Queen.
    static public int[] pieceMoveCost = { 0, 25, 38, 50, 50, 125, 175 };

    //Interest level of a move adds additional levels of recursion to a move
    public enum Interest
    {
        NONE = 0,
        LOW = 0,
        MEDIUM = 1,
        HIGH = 2,
    }

    //Scores awarded for various states of the game.
    const int checkValue = 100;
    const int checkMateValue = 1000000;
    const int potentialCheckmateValue = 400;
    const int drawValue = -10000000;

    //Bonuses given to special moves.
    const int promotionBonus = 100;
    const int enPassantBonus = 100;
    const int castleBonus = 75;

    //Base level of recursion to use when evaluating a move.
    const int baseMoveDepth = 3;

    //Whether its the players turn or not.
    static bool isMyTurn;
    //If the turn is white, so it can be checked if the player is black or white
    static bool isWhite;
    //Multiplier that sets preference to protecting pieces on the players turn.
    const float myPieceMultiplier = 1.4f;

    //Clamps the leverage within a certain range so the results are not as extreme
    const float leverageClamp = 1.5f;
    //Bonus applied for achieving a higher leverage in a move (Should be multiplied by leverage)
    const int leverageBonus = 125;

    //An evaluated move with a score, a level of interest and an assigned move
    public struct EvaluatedMove
    {
        //Score - How valuable this move is to the current player.
        public int score;
        //Intrest - How intresting a move is which can be used as a bonus to provide deeper levels of recursion.
        public Interest interest;
        //Move - The move that is made using the chess challenge API.
        public Move move;

        public EvaluatedMove(Move _move)
        {
            score = 0;
            interest = Interest.NONE;
            move = _move;
        }
    }

    public Move Think(Board board, Timer timer)
    {
        //Upon begining to think it should be the players turn.
        isMyTurn = true;
        isWhite = board.IsWhiteToMove;

        //Get All Moves
        Span<Move> moveSpan = stackalloc Move[256];
        board.GetLegalMovesNonAlloc(ref moveSpan);

        Span<EvaluatedMove> evaluatedMovesSpan = stackalloc EvaluatedMove[moveSpan.Length];
        var evaluatedMoves = EvaluateMoves(moveSpan, board, timer, evaluatedMovesSpan);

        //Get the highest scoring move from all the evaluated moves, this will (hopefully) be the optimal move
        EvaluatedMove bestMove = GetBestMove(evaluatedMoves);

        Console.WriteLine($"Best move score: {bestMove.score} - Leverage: {CalculateLeverage(board)}");
        return bestMove.move;
    }

    public Span<EvaluatedMove> EvaluateMoves(Span<Move> moveSpan, Board board, Timer timer, Span<EvaluatedMove> evaluatedMovesSpan, int currentDepth = 1, int recursiveDepth = baseMoveDepth)
    {
        //Consider all legal moves, with current and max depth set, by default this is base depth
        for (int i = 0; i < evaluatedMovesSpan.Length; i++)
        {
            evaluatedMovesSpan[i] = EvaluateMove(moveSpan[i], board, timer, currentDepth, recursiveDepth);
        }

        return evaluatedMovesSpan;
    }

    public static EvaluatedMove GetBestMove(Span<EvaluatedMove> evaluatedMoves)
    {
        //Chooses a random move, if no move is deemed better, this is the move that is made.
        Random rng = new();
        EvaluatedMove bestMove = evaluatedMoves[rng.Next(evaluatedMoves.Length)];

        //Gets the best move
        for (int i = 0; i < evaluatedMoves.Length; i++)
        {
            if (isMyTurn)
            {
                if (evaluatedMoves[i].score > bestMove.score)
                    bestMove = evaluatedMoves[i];
            }
            else
            {
                if (evaluatedMoves[i].score < bestMove.score)
                    bestMove = evaluatedMoves[i];
            }
        }

        return bestMove;
    }

    public EvaluatedMove EvaluateMove(Move move, Board board, Timer timer, int currentDepth, int recursiveDepth = baseMoveDepth)
    {
        EvaluatedMove evalMove = new(move);
        float score = 0;

        //If the move is a capture move, how valuable would the captured piece be?
        if (move.IsCapture)
        {
           score += EvaluatePieceValue(pieceValues[(int)move.CapturePieceType], board);

            if (move.CapturePieceType == PieceType.Queen)
                evalMove.interest = Interest.MEDIUM;
        }

        //Each piece has a movement cost, to discourage throwing valuable pieces at the enemy
        score -= pieceMoveCost[(int)move.MovePieceType];

        //================================================
        //Checks next move to see if its advantagous
        board.MakeMove(move);
        isMyTurn = !isMyTurn;

        if (board.IsInCheck())
        {
            evalMove.interest = Interest.HIGH;
            score += checkValue;
        }

        if (board.IsInCheckmate())
        {
            if (currentDepth == 1)
                score += checkMateValue;
            else
                score += potentialCheckmateValue;
        }

        if (board.IsDraw())
        {
            score += drawValue;
        }

        isMyTurn = !isMyTurn;
        board.UndoMove(move);
        //================================================

        //================================================
        //Bonus scores given to special moves
        if (move.IsPromotion)
            score += promotionBonus;

        if (move.IsEnPassant)
            score += enPassantBonus;

        if (move.IsCastles)
            score += castleBonus;
        //===============================================

        //Moves that are made from the other player are negative, and are retracted from a moves scoring
        //Moves that are made from the other player are negative, and are retracted from a moves scoring
        evalMove.score /= currentDepth;
        evalMove.score += (int)(score * TurnMultipler);

        //Bonus depth is added depending on how interesting that move was + time must be above 5 seconds to prevent timeout
        int depthToExplore = currentDepth == 1 && timer.MillisecondsRemaining > 5000 ? recursiveDepth + (int)evalMove.interest : recursiveDepth;

        //THE SECRET SAUCE // Moves are checked recursively to decide whether a move would be good or not, the moves alternate players
        if (currentDepth < recursiveDepth)
        {
            board.MakeMove(move);
            isMyTurn = !isMyTurn;

            Span<Move> moveSpan = stackalloc Move[256];
            board.GetLegalMovesNonAlloc(ref moveSpan);

            if (moveSpan.Length > 0)
            {
                Span<EvaluatedMove> evaluatedMovesSpan = stackalloc EvaluatedMove[moveSpan.Length];

                EvaluatedMove nextBestMove = GetBestMove(EvaluateMoves(moveSpan, board, timer, evaluatedMovesSpan, currentDepth + 1, depthToExplore));
                evalMove.score += nextBestMove.score;
            }

            board.UndoMove(move);
            isMyTurn = !isMyTurn;
        }

        return evalMove;
    }

    //When its the enemies turn the scores are flipped.
    static float TurnMultipler => isMyTurn ? 1f : -1f;

    //Multplier that prioritises protecting high value pieces over taking pieces.
    static int EvaluatePieceValue(int pieceValue, Board board)
    {
        if (isMyTurn)
            return (int)(pieceValue);
        else
            return (int)(myPieceMultiplier * pieceValue);
    }

    static float CalculateLeverage(Board board)
    {
        PieceList[] pieceLists = board.GetAllPieceLists();
        Span<PieceList> pieceListSpan = pieceLists;

        int whitePiecesValue = 1;
        int blackPiecesValue = 1;

        foreach (PieceList pieceList in pieceListSpan)
        {
            if (pieceList.IsWhitePieceList)
            {
                whitePiecesValue += CalculatePieceListValue(pieceList);
                continue;
            }
            else
            {
                blackPiecesValue += CalculatePieceListValue(pieceList);
                continue;
            }
        }

        float leverage;

        if (isWhite)
            leverage = (float)whitePiecesValue / (float)blackPiecesValue;
        else
            leverage = (float)blackPiecesValue / (float)whitePiecesValue;

        Math.Clamp(leverage, -leverageClamp, leverageClamp);
        return leverage;
    }

    static int CalculatePieceListValue(PieceList pieceList)
    {
        int pieceListValue = 1;
        
        for (int i = 0; i < pieceList.Count; i++)
        {
            pieceListValue += pieceValues[(int)pieceList.GetPiece(i).PieceType];
        }

        return pieceListValue;
    }
}