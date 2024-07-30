using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace Cezanne.Core.Tests.Rule
{
    [AttributeUsage(AttributeTargets.Method)]
    public class TempFolderAttribute : Attribute, ITestAction
    {
        public void BeforeTest(ITest test)
        {
            if (
                test.Fixture is ITempFolder tempFolder
                && test.Method?.GetCustomAttributes<TempFolderAttribute>(false).Length > 0
            )
            {
                var name = test.FullName.Contains('(')
                    ? test.FullName[0..test.FullName.IndexOf('(')]
                    : test.FullName;
                tempFolder.Temp = Path.Combine(
                    Path.GetTempPath(),
                    $"{name}-{(test as Test)!.Seed}-{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()}"
                );
                Directory.CreateDirectory(tempFolder.Temp);
            }
        }

        public void AfterTest(ITest test)
        {
            if (test.Fixture is not ITempFolder { Temp: not null } tempFolder)
            {
                return;
            }

            Directory.Delete(tempFolder.Temp, true);
            tempFolder.Temp = null;
        }

        public ActionTargets Targets => ActionTargets.Test | ActionTargets.Suite;
    }
}
