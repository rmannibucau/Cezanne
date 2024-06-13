using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace Cézanne.Core.Tests.Rule
{
    [AttributeUsage(AttributeTargets.Method)]
    public class TempFolderAttribute : Attribute, ITestAction
    {
        public void BeforeTest(ITest test)
        {
            if (test.Fixture is ITempFolder tempFolder &&
                test.Method?.GetCustomAttributes<TempFolderAttribute>(false).Length > 0)
            {
                tempFolder.Temp = Path.Combine(Path.GetTempPath(), $"{test.FullName}-{(test as Test)!.Seed}");
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