using System;

namespace TokRun
{
    internal static class Logger
    {
        public static bool VerboseEnabled;

        public static void Info(string message)
        {
            Console.WriteLine(message);
        }

        public static void Info(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }

        public static void Error(string message)
        {
            Console.Error.WriteLine(message);
        }

        public static void Error(string format, params object[] args)
        {
            Console.Error.WriteLine(format, args);
        }

        public static void Verbose()
        {
            if (VerboseEnabled)
                Console.WriteLine();
        }

        public static void Verbose(string message)
        {
            if (VerboseEnabled)
                Console.WriteLine(message);
        }

        public static void Verbose(string format, params object[] args)
        {
            if (VerboseEnabled)
                Console.WriteLine(format, args);
        }
    }
}
