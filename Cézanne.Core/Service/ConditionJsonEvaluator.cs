using Cézanne.Core.Descriptor;
using Json.More;
using Json.Pointer;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Cézanne.Core.Service;

public class ConditionJsonEvaluator(ILogger<ConditionJsonEvaluator> logger)
{
    public bool Evaluate(Manifest.AwaitCondition condition, JsonElement body)
    {
        switch (condition.TypeValue)
        {
            case Manifest.AwaitConditionType.JsonPointer:
                var pointer = JsonPointer.Parse(condition.Pointer ?? "");
                var json = pointer.Evaluate(body);
                if (json is null || !json.HasValue)
                {
                    logger.LogDebug("No result for {condition} from JSON={body} and evaluation={json}", condition, body, json);
                    return false;
                }

                var evaluated = json.Value.ToString();
                var result = _Evaluate(condition.OperatorType ?? Manifest.JsonPointerOperator.EqualsValue, condition.Value?.ToString() ?? "", evaluated);
                logger.LogDebug("{condition}={result} from JSON={body} and evaluation={json}", condition, result, body, json);
                return result;
            case Manifest.AwaitConditionType.StatusCondition:
                var conditions = JsonPointer.Parse("/status/conditions").Evaluate(body);
                if (conditions is null || !conditions.HasValue || conditions.Value.ValueKind != JsonValueKind.Array)
                {
                    logger.LogDebug("No conditions from {body}", body);
                    return false;
                }
                var eval = conditions.Value.EnumerateArray()
                    .Where(it => it.ValueKind == JsonValueKind.Object)
                    .Select(it => it.AsNode()?.AsObject())
                    .Any(it => it!.TryGetPropertyValue("type", out var type) &&
                                type?.GetValueKind() == JsonValueKind.String &&
                                type.ToString() == condition.ConditionType &&
                               it.TryGetPropertyValue("status", out var status) &&
                                status?.ToString() == (condition.Value?.ToString() ?? ""));
                logger.LogDebug("{condition}={eval} from JSON={body}", condition, eval, body);
                return eval;
            default:
                throw new ArgumentException($"Unsupported value '{condition.TypeValue}'", nameof(condition));
        }
    }

    private bool _Evaluate(Manifest.JsonPointerOperator type, string value, string evaluated)
    {
        return type switch
        {
            Manifest.JsonPointerOperator.EqualsValue => value == evaluated,
            Manifest.JsonPointerOperator.NotEquals => value != evaluated,
            Manifest.JsonPointerOperator.EqualsIgnoreCase => string.Equals(evaluated, value, StringComparison.OrdinalIgnoreCase),
            Manifest.JsonPointerOperator.NotEqualsIgnoreCase => !string.Equals(evaluated, value, StringComparison.OrdinalIgnoreCase),
            Manifest.JsonPointerOperator.Contains => evaluated.Contains(value),
            Manifest.JsonPointerOperator.Exists => true,
            _ => throw new ArgumentException($"Unsupported comparison type: {type}", nameof(type)),
        };

    }
}
