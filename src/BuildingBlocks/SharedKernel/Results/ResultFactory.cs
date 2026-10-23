using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace Evidgate.BuildingBlocks.SharedKernel.Results;

/// <summary>
/// The generic plumbing the C2 pipeline needs because its behaviours are closed
/// over an unknown response type: answer with a failed <see cref="Result"/> /
/// <see cref="Result{T}"/> of that closed type, read the failure out of a returned
/// response, and round-trip a success through the idempotency store (00 §5.5 C2,
/// 00 §9.1). The per-type delegates are built once and cached; a response type that
/// does not carry the C8 contract is rejected loudly rather than passed through.
/// </summary>
public static class ResultFactory
{
    private static readonly ConcurrentDictionary<Type, Func<Error, object>> FailureFactories = new();

    private static readonly ConcurrentDictionary<Type, Func<int, string, object>> ReviveFactories = new();

    private static readonly ConcurrentDictionary<Type, Func<object?, string>> ValueSerializers = new();

    /// <summary>
    /// True when <paramref name="responseType"/> carries the C8 Result contract —
    /// the pipeline's marker for a business message (00 §5.5 C8). The skeleton
    /// slice's DTO messages (01.1.2-T3) are deliberately outside it.
    /// </summary>
    public static bool IsResultContract(Type responseType)
    {
        ArgumentNullException.ThrowIfNull(responseType);

        return typeof(Result).IsAssignableFrom(responseType);
    }

    /// <summary>A failed response of the closed type <typeparamref name="TResponse"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResponse"/> is not a <see cref="Result"/>/<see cref="Result{T}"/> —
    /// a pipeline stage can only fail a message that carries the C8 contract.
    /// </exception>
    public static TResponse Failure<TResponse>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return (TResponse)FailureFactories.GetOrAdd(typeof(TResponse), CreateFailureFactory)(error);
    }

    /// <summary>
    /// True when <paramref name="response"/> is a failed <see cref="Result"/>, with
    /// its <see cref="Error"/> — the tenant transaction behaviour rolls back and the
    /// idempotency behaviour stores nothing for these (00 §5.5 C2).
    /// </summary>
    public static bool TryGetFailure<TResponse>(TResponse response, [NotNullWhen(true)] out Error? error)
    {
        if (response is Result { IsSuccess: false, Error: { } failure })
        {
            error = failure;
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// The status and JSON body the idempotency store keeps for a successful
    /// response (the <c>CompleteAsync</c> call of 01.5.1-T6's store); the body is the
    /// success value alone, so <see cref="Revive{TResponse}"/> can rebuild the very
    /// response the original request returned.
    /// </summary>
    public static (int Status, string Body) Describe<TResponse>(TResponse response)
    {
        if (response is not Result { IsSuccess: true } result)
        {
            throw new InvalidOperationException(
                $"{typeof(TResponse)} is not a successful {nameof(Result)}: only a successful C8 response is stored for replay.");
        }

        var valueType = DeclaredValueType(typeof(TResponse));
        var body = valueType is null
            ? "null"
            : ValueSerializers.GetOrAdd(valueType, type => value => JsonSerializer.Serialize(value, type))(result.SuccessValue);

        return (result.Status, body);
    }

    /// <summary>Rebuilds a stored successful response (the idempotency replay path).</summary>
    public static TResponse Revive<TResponse>(int status, string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return (TResponse)ReviveFactories.GetOrAdd(typeof(TResponse), CreateReviveFactory)(status, body);
    }

    private static Func<Error, object> CreateFailureFactory(Type responseType)
    {
        if (responseType == typeof(Result))
        {
            return error => Result.Failure(error);
        }

        var valueType = DeclaredValueType(responseType) ?? throw NotAContract(responseType);

        return (Func<Error, object>)GenericFactory(nameof(FailureOf), valueType).Invoke(null, null)!;
    }

    private static Func<int, string, object> CreateReviveFactory(Type responseType)
    {
        if (responseType == typeof(Result))
        {
            return (status, _) => Result.Success(status);
        }

        var valueType = DeclaredValueType(responseType) ?? throw NotAContract(responseType);

        return (Func<int, string, object>)GenericFactory(nameof(ReviveOf), valueType).Invoke(null, null)!;
    }

    private static Func<Error, object> FailureOf<T>() => error => Result.Failure<T>(error);

    private static Func<int, string, object> ReviveOf<T>() =>
        (status, body) => Result.Success(JsonSerializer.Deserialize<T>(body)!, status);

    private static MethodInfo GenericFactory(string name, Type valueType) =>
        typeof(ResultFactory).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(valueType);

    /// <summary>The closed <c>Result&lt;T&gt;</c>'s T, or null when the type is not one.</summary>
    private static Type? DeclaredValueType(Type responseType) =>
        responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>)
            ? responseType.GetGenericArguments()[0]
            : null;

    private static InvalidOperationException NotAContract(Type responseType) =>
        new($"{responseType} does not carry the C8 Result contract (00 §5.5 C8): "
            + "the pipeline's Result stages only run for Result/Result<T> responses.");
}
