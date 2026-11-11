using System;

namespace IronOath.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            CombatTests.Run(Check);
            Console.WriteLine($"Iron Oath: {_passed} passed, {_failed} failed.");
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                Console.Error.WriteLine("FAIL " + name + ": " + exception);
            }
        }
    }
}
