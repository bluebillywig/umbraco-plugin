using System.Reflection;
using System.Text.Json;
using BlueBillywig.Umbraco.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>
/// The backoffice's API error interceptor replaces any non-2xx body that is not problem details (type, title and status all
/// present) by a bare "Bad Request"-style problem, so every refusal has to leave the server as problem details that carry
/// the sentence as <c>detail</c> and <c>error</c>.
/// </summary>
public class RefusalShapeTests
{
    private static IActionResult Run(IActionResult result)
    {
        var http = new DefaultHttpContext();
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(actionContext, [], result, new object());
        new RefusalShapeAttribute().OnResultExecuting(context);
        return context.Result;
    }

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var shaped = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, shaped.StatusCode);
        return Assert.IsAssignableFrom<ProblemDetails>(shaped.Value);
    }

    [Fact]
    public void The_filter_is_on_the_base_class_so_every_controller_has_it()
    {
        Assert.NotNull(typeof(BlueBillywigControllerBase).GetCustomAttribute<RefusalShapeAttribute>(true));
        Assert.All(
            typeof(BlueBillywigControllerBase).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(BlueBillywigControllerBase))),
            t => Assert.NotNull(t.GetCustomAttribute<RefusalShapeAttribute>(true)));
    }

    [Fact]
    public void A_bad_request_with_an_error_body_becomes_problem_details_with_the_sentence()
    {
        ProblemDetails problem = Problem(Run(new BadRequestObjectResult(new { error = "Invalid playout name" })), 400);

        Assert.Equal("Invalid playout name", problem.Detail);
        Assert.Equal("Invalid playout name", problem.Extensions["error"]);
        Assert.False(string.IsNullOrEmpty(problem.Type));
        Assert.Equal("Bad Request", problem.Title);
        Assert.Equal(400, problem.Status);
    }

    [Fact]
    public void The_shaped_body_serialises_with_type_title_status_detail_and_error()
    {
        ProblemDetails problem = Problem(Run(new BadRequestObjectResult(new { error = "Nope" })), 400);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(problem));
        JsonElement root = json.RootElement;
        Assert.True(root.TryGetProperty("type", out _));
        Assert.True(root.TryGetProperty("title", out _));
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal("Nope", root.GetProperty("detail").GetString());
        Assert.Equal("Nope", root.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(403)]
    public void Other_error_statuses_keep_their_status_and_get_the_sentence(int status)
    {
        ProblemDetails problem = Problem(Run(new ObjectResult(new { error = "Because" }) { StatusCode = status }), status);

        Assert.Equal("Because", problem.Detail);
        Assert.Equal("Because", problem.Extensions["error"]);
    }

    [Fact]
    public void Existing_problem_details_keep_their_fields_and_gain_the_error_extension()
    {
        var original = new ProblemDetails { Type = "BlueBillywig.Refused", Status = 409, Title = "Conflict", Detail = "Changed meanwhile" };
        original.Extensions["code"] = "x";

        ProblemDetails problem = Problem(Run(new ObjectResult(original) { StatusCode = 409 }), 409);

        Assert.Equal("Conflict", problem.Title);
        Assert.Equal("Changed meanwhile", problem.Detail);
        Assert.Equal("Changed meanwhile", problem.Extensions["error"]);
        Assert.Equal("x", problem.Extensions["code"]);
    }

    [Fact]
    public void Automatic_model_validation_uses_the_first_message_as_the_sentence()
    {
        var validation = new ValidationProblemDetails(new Dictionary<string, string[]> { ["request"] = ["A non-empty request body is required."], ["other"] = ["second"] })
        {
            Status = 400,
            Title = "One or more validation errors occurred.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        };

        ProblemDetails problem = Problem(Run(new BadRequestObjectResult(validation)), 400);

        Assert.Equal("A non-empty request body is required.", problem.Detail);
        Assert.Equal("A non-empty request body is required.", problem.Extensions["error"]);
    }

    [Fact]
    public void An_error_body_without_a_sentence_still_becomes_valid_problem_details()
    {
        ProblemDetails problem = Problem(Run(new ObjectResult(new { nothing = 1 }) { StatusCode = 400 }), 400);

        Assert.Null(problem.Detail);
        Assert.False(problem.Extensions.ContainsKey("error"));
        Assert.Equal("Bad Request", problem.Title);
    }

    [Fact]
    public void Successes_and_non_object_results_are_left_alone()
    {
        var ok = new OkObjectResult(new { error = "not an error" });
        Assert.Same(ok, Run(ok));

        var status = new StatusCodeResult(401);
        Assert.Same(status, Run(status));
        var content = new ContentResult { Content = "{}", StatusCode = 502 };
        Assert.Same(content, Run(content));
    }
}
