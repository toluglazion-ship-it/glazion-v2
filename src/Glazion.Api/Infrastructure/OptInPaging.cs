using System.Reflection;
using Glazion.Api.Contracts;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Glazion.Api.Infrastructure;

/// <summary>
/// Marks a list endpoint whose paging is opt-in: a plain array without cursor or pageSize, a PagedResult with them.
/// Swagger then shows both shapes instead of only the array.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OptInPagingAttribute(Type itemType) : Attribute
{
    public Type ItemType { get; } = itemType;
}

public sealed class OptInPagingOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var attribute = context.MethodInfo.GetCustomAttribute<OptInPagingAttribute>();
        if (attribute is null) return;

        var array = context.SchemaGenerator.GenerateSchema(typeof(IReadOnlyList<>).MakeGenericType(attribute.ItemType), context.SchemaRepository);
        var paged = context.SchemaGenerator.GenerateSchema(typeof(PagedResult<>).MakeGenericType(attribute.ItemType), context.SchemaRepository);
        operation.Responses ??= new OpenApiResponses();
        operation.Responses["200"] = new OpenApiResponse
        {
            Description = "Without cursor or pageSize: a plain array (at most 500 items). With either: { items, totalCount, nextCursor }; " +
                          "pass nextCursor back as cursor until it is null.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new() { Schema = new OpenApiSchema { OneOf = [array, paged] } }
            }
        };

        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is not OpenApiParameter p) continue;
            if (p.Name == "pageSize") p.Description = "Turns on paging and sets the page size.";
            if (p.Name == "cursor") p.Description = "nextCursor from the previous page. Turns on paging.";
        }
    }
}
