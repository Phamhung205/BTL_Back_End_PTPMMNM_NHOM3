using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Models;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize]
[Route("api/projects/{projectId:long}/translations")]
public sealed class TranslationsController(ProjectAccessService access, TranslationService translations) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(long projectId,[FromQuery]string locale,[FromQuery]string? q,[FromQuery]string? status,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await translations.ListAsync(projectId,locale,q,status,ct));
    }

    [HttpPost]
    public async Task<IActionResult> Save(long projectId,SaveTranslationRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireAnyAsync(projectId,userId,[ProjectRole.Manager,ProjectRole.Translator],ct);
        return Ok(await translations.SaveAsync(projectId,userId,request,ct));
    }

    [HttpPut("{translationId:long}/assign")]
    public async Task<IActionResult> Assign(long projectId,long translationId,AssignTranslationRequest request,CancellationToken ct)
    {
        await access.RequireManagerAsync(projectId,User.UserId(),ct);
        await translations.AssignAsync(projectId,translationId,request.UserId,request.ExpectedRowVersion,ct);
        return Ok(new { success=true });
    }

    [HttpPost("{translationId:long}/comments")]
    public async Task<IActionResult> Comment(long projectId,long translationId,AddCommentRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.GetRoleAsync(projectId,userId,ct);
        await translations.AddCommentAsync(projectId,translationId,userId,request.Body,ct);
        return Ok(new { success=true });
    }

    [HttpPost("{translationId:long}/approve")]
    public async Task<IActionResult> Approve(long projectId,long translationId,ReviewRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireAnyAsync(projectId,userId,[ProjectRole.Manager,ProjectRole.Reviewer],ct);
        await translations.ReviewAsync(projectId,translationId,userId,true,request.Note,request.ExpectedRowVersion,ct);
        return Ok(new { success=true,status=TranslationStatus.Approved });
    }

    [HttpPost("{translationId:long}/reject")]
    public async Task<IActionResult> Reject(long projectId,long translationId,ReviewRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireAnyAsync(projectId,userId,[ProjectRole.Manager,ProjectRole.Reviewer],ct);
        await translations.ReviewAsync(projectId,translationId,userId,false,request.Note,request.ExpectedRowVersion,ct);
        return Ok(new { success=true,status=TranslationStatus.NeedsReview });
    }
}
