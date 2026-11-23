using FluentValidation;
using LedgerPay.Api.EventStore;
using LedgerPay.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LedgerPay.Api.Infrastructure;

public class ErrorHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        ProblemDetails? problem = exception switch
        {
            ValidationException v => new ValidationProblemDetails(v.Errors.GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())) { Status = 400, Title = "Validation failed" },
            DomainException d => new ProblemDetails { Status = 409, Title = d.Message },
            ConcurrencyException => new ProblemDetails { Status = 409, Title = "Too many concurrent changes; please retry." },
            KeyNotFoundException => new ProblemDetails { Status = 404, Title = "Not found" },
            _ => null,
        };
        if (problem is null) return false;
        http.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = exception });
    }
}
