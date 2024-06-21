using System.Collections.Specialized;
using System.Configuration;
using Cézanne.Core.Descriptor;

namespace Cézanne.Core.Service
{
    public class ConditionEvaluator
    {
        public bool Test(Manifest.Conditions? conditions)
        {
            return conditions is null
                || _ToOperator(
                    conditions.OperatorType ?? Manifest.ConditionOperator.All,
                    (conditions.ConditionsList ?? []).Select(_Evaluate)
                );
        }

        private bool _Evaluate(Manifest.Condition condition)
        {
            var evaluationResult =
                string.IsNullOrEmpty(condition.Key)
                || _ToValue(condition.Value)
                    == _Read(condition.Type ?? Manifest.ConditionType.Env, condition.Key);
            return (condition.Negate ?? false) != evaluationResult;
        }

        private string _Read(Manifest.ConditionType type, string key)
        {
            return type switch
            {
                Manifest.ConditionType.Env => Environment.GetEnvironmentVariable(key) ?? "",
                Manifest.ConditionType.SystemProperty => _ReadSetting(key) ?? "",
                _ => throw new ArgumentException($"Invalid condition type {type}", nameof(type))
            };
        }

        private string? _ReadSetting(string key)
        {
            try
            {
                return (ConfigurationManager.GetSection("cezanne") as NameValueCollection)?[key];
            }
            catch (ConfigurationErrorsException)
            {
                try
                {
                    return ConfigurationManager.AppSettings?[key];
                }
                catch (ConfigurationErrorsException)
                {
                    return null;
                }
            }
        }

        private string _ToValue(string? value)
        {
            return value ?? "true";
        }

        private bool _ToOperator(Manifest.ConditionOperator operatorType, IEnumerable<bool> stream)
        {
            return operatorType == Manifest.ConditionOperator.Any
                ? stream.Any(b => b)
                : stream.All(b => b);
        }
    }
}
