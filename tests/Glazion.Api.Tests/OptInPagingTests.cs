using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Controllers;
using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Glazion.Api.Tests;

/// <summary>Swagger shows both shapes of an opt-in paged list: the plain array and PagedResult.</summary>
public sealed class OptInPagingTests
{
    [Theory]
    [InlineData(nameof(AdminController.BaseGlazes), "BaseGlazeResponse")]
    [InlineData(nameof(AdminController.CodeRecipes), "RecipeDto")]
    public void Swagger_offers_the_array_and_the_paged_result(string action, string itemSchema)
    {
        var generator = new SchemaGenerator(new SchemaGeneratorOptions(), new JsonSerializerDataContractResolver(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var repository = new SchemaRepository();
        var method = typeof(AdminController).GetMethod(action)!;
        var operation = new OpenApiOperation
        {
            Parameters = [new OpenApiParameter { Name = "cursor" }, new OpenApiParameter { Name = "pageSize" }]
        };
        new OptInPagingOperationFilter().Apply(operation, new OperationFilterContext(new ApiDescription(), generator, repository, new OpenApiDocument(), method));

        var response = operation.Responses!["200"];
        Assert.Contains("nextCursor", response.Description);
        var oneOf = response.Content!["application/json"].Schema!.OneOf!;
        Assert.Equal(2, oneOf.Count);
        Assert.Equal(JsonSchemaType.Array, oneOf[0].Type);
        Assert.Contains(repository.Schemas.Keys, k => k.Contains("PagedResult") && k.Contains(itemSchema));
        Assert.All(operation.Parameters!, p => Assert.False(string.IsNullOrEmpty(p.Description)));
    }
}
