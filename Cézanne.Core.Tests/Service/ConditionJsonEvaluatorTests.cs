using Cézanne.Core.Descriptor;
using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Cézanne.Core.Tests.Service
{
    public class ConditionJsonEvaluatorTests
    {
        private readonly ConditionJsonEvaluator _conditionJsonEvaluator =
            new(new Logger<ConditionJsonEvaluator>(new NullLoggerFactory()));

        [TestCaseSource(nameof(DataSet))]
        public bool Eval(string condition, string input)
        {
            var cond =
                JsonSerializer.Deserialize<Manifest.AwaitCondition>(condition, Jsons.Options) ??
                throw new ArgumentNullException(nameof(condition));
            var data = JsonSerializer.Deserialize<JsonElement>(input);
            try
            {
                return _conditionJsonEvaluator.Evaluate(cond, data);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IEnumerable<TestCaseData> DataSet()
        {
            yield return new TestCaseData(
                    "{\"type\":\"JSON_POINTER\",\"pointer\":\"/v\",\"value\":1}",
                    "{\"v\":1}")
                .Returns(true);
            yield return new TestCaseData(
                    "{\"type\":\"JSON_POINTER\",\"pointer\":\"/v\",\"value\":\"foo\"}",
                    "{\"v\":\"bar\"}")
                .Returns(false);
            yield return new TestCaseData(
                    "{\"type\":\"JSON_POINTER\",\"pointer\":\"/v\",\"value\":\"foo\"}",
                    "{\"v\":\"foo\"}")
                .Returns(true);
            yield return new TestCaseData(
                    "{\"type\":\"JSON_POINTER\",\"pointer\":\"/v\",\"value\":\"foo\"}",
                    "{}")
                .Returns(false);
            yield return new TestCaseData(
                    "{\"type\":\"STATUS_CONDITION\",\"conditionType\":\"Ready\",\"value\":\"true\"}",
                    "{\"status\":{\"conditions\":[{\"type\":\"Ready\",\"status\":true}]}}")
                .Returns(true);
            yield return new TestCaseData(
                    "{\"type\":\"STATUS_CONDITION\",\"conditionType\":\"Ready\",\"value\":\"true\"}",
                    "{\"status\":{\"conditions\":[{\"type\":\"Foo\",\"status\":true},{\"type\":\"Ready\",\"status\":true}]}}")
                .Returns(true);
            yield return new TestCaseData(
                    "{\"type\":\"STATUS_CONDITION\",\"conditionType\":\"Ready\",\"value\":\"true\"}",
                    "{\"status\":{\"conditions\":[{\"type\":\"Foo\",\"status\":true},{\"type\":\"Ready\",\"status\":false}]}}")
                .Returns(false);
            yield return new TestCaseData(
                    "{\"type\":\"STATUS_CONDITION\",\"conditionType\":\"Ready\",\"value\":\"true\"}",
                    "{\"status\":{\"conditions\":[{\"type\":\"Foo\",\"status\":true}]}}")
                .Returns(false);
            yield return new TestCaseData(
                    "{\"type\":\"STATUS_CONDITION\",\"conditionType\":\"Ready\",\"value\":\"true\"}",
                    "{\"status\":{\"conditions\":[{\"type\":\"Foo\",\"status\":true},{\"status\":true}]}}")
                .Returns(false);
            yield return new TestCaseData(
                    "{\"type\":\"STATUS_CONDITION\",\"conditionType\":\"Ready\",\"value\":\"true\"}",
                    "{\"status\":{\"conditions\":[]}}")
                .Returns(false);
            yield return new TestCaseData(
                    "{\"type\":\"STATUS_CONDITION\",\"conditionType\":\"Ready\",\"value\":\"true\"}",
                    "{\"status\":{}}")
                .Returns(false);
            yield return new TestCaseData(
                    "{\"type\":\"STATUS_CONDITION\",\"conditionType\":\"Ready\",\"value\":\"true\"}",
                    "{}")
                .Returns(false);
        }
    }
}