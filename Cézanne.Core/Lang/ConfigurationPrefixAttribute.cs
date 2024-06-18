namespace Cézanne.Core.Lang;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class ConfigurationPrefixAttribute(string value) : Attribute {
    public readonly string Value = value;
}