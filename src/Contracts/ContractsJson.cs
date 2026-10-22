using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contracts;

/// <summary>Source-generated serializer for the messages: no reflection on the hot path.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PaymentCaptured))]
public sealed partial class ContractsJson : JsonSerializerContext
{
}
