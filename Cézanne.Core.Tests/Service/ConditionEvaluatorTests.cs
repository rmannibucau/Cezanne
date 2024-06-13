using Cézanne.Core.Descriptor;
using Cézanne.Core.Service;

namespace Cézanne.Core.Tests.Service
{
    public class ConditionEvaluatorTests
    {
        private readonly ConditionEvaluator _conditionEvaluator = new();

        [OneTimeSetUp]
        public void SetUp()
        {
            Environment.SetEnvironmentVariable("TEST_ENV_VAR", "set");
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("TEST_ENV_VAR", null);
        }

        [TestCaseSource(nameof(Dataset))]
        public bool Test(Manifest.Conditions conditions)
        {
            return _conditionEvaluator.Test(conditions);
        }


        private static IEnumerable<TestCaseData> Dataset()
        {
            yield return new TestCaseData(new Manifest.Conditions
            {
                OperatorType = Manifest.ConditionOperator.All, ConditionsList = []
            }).Returns(true);
            yield return new TestCaseData(new Manifest.Conditions
            {
                OperatorType = Manifest.ConditionOperator.All,
                ConditionsList =
                [
                    new Manifest.Condition { Type = Manifest.ConditionType.Env, Key = "TEST_ENV_VAR", Value = "set" }
                ]
            }).Returns(true);
            yield return new TestCaseData(new Manifest.Conditions
            {
                OperatorType = Manifest.ConditionOperator.All,
                ConditionsList =
                [
                    new Manifest.Condition { Type = Manifest.ConditionType.Env, Key = "TEST_ENV_VAR", Value = "set2" }
                ]
            }).Returns(false);
            yield return new TestCaseData(new Manifest.Conditions
            {
                OperatorType = Manifest.ConditionOperator.All,
                ConditionsList =
                [
                    new Manifest.Condition
                    {
                        Type = Manifest.ConditionType.Env, Key = "TEST_ENV_VAR", Value = "set2", Negate = true
                    }
                ]
            }).Returns(true);
            yield return new TestCaseData(new Manifest.Conditions
            {
                OperatorType = Manifest.ConditionOperator.Any, ConditionsList = []
            }).Returns(false);
            yield return new TestCaseData(new Manifest.Conditions()).Returns(true);
        }
    }
}