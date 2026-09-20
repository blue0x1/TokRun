using System;

namespace TokRun
{
    internal sealed class TokenInfo
    {
        public IntPtr Token;
        public string UserName;
        public int Pid;
        public string ProcessName;
        public int SessionId;
        public string TokenType;
        public string ImpersonationLevel;
        public bool IsLinkedToken;

        public int Score(int preferredSession)
        {
            int score = 0;
            if (SessionId == preferredSession) score += 1000;
            if (SessionId > 0) score += 200;
            if (String.Equals(TokenType, "Primary", StringComparison.OrdinalIgnoreCase)) score += 100;
            if (!IsLinkedToken) score += 10;
            return score;
        }
    }
}
