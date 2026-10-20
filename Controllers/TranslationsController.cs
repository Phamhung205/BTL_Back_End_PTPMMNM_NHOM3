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
    /// <summary>Danh sách khóa + bản dịch theo ngôn ngữ. assignee: me | none | {userId}.</summary>
    [HttpGet]
    public async Task<IActionResult> List(long projectId,[FromQuery]string? locale,[FromQuery]string? q,[FromQuery]string? status,[FromQuery]string? assignee,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.GetRoleAsync(projectId,userId,ct);
        return Ok(await translations.ListAsync(projectId,userId,locale,q,status,assignee,ct));
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(long projectId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await translations.StatsAsync(projectId,ct));
    }

    /// <summary>Lưu nháp (status=draft) hoặc gửi review (status=translated).</summary>
    [HttpPost]
    public async Task<IActionResult> Save(long projectId,SaveTranslationRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        var role=await access.GetRoleAsync(projectId,userId,ct);
        if(role is not (ProjectRole.Manager or ProjectRole.Translator))
            throw ApiException.Forbidden("Chỉ manager hoặc translator được sửa bản dịch.");
        return Ok(await translations.SaveAsync(projectId,userId,role,request,ct));
    }

    [HttpPut("{translationId:long}/assign")]
    public async Task<IActionResult> Assign(long projectId,long translationId,AssignTranslationRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireManagerAsync(projectId,userId,ct);
        return Ok(await translations.AssignAsync(projectId,userId,translationId,request.UserId,request.ExpectedRowVersion,ct));
    }

    /// <summary>Phân công nhiều khóa (kể cả khóa chưa có bản dịch) cho một người dịch.</summary>
    [HttpPut("assign")]
    public async Task<IActionResult> BulkAssign(long projectId,BulkAssignRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireManagerAsync(projectId,userId,ct);
        return Ok(await translations.BulkAssignAsync(projectId,userId,request,ct));
    }

    [HttpGet("{translationId:long}/comments")]
    public async Task<IActionResult> Comments(long projectId,long translationId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await translations.ListCommentsAsync(projectId,translationId,ct));
    }

    [HttpPost("{translationId:long}/comments")]
    public async Task<IActionResult> Comment(long projectId,long translationId,AddCommentRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.GetRoleAsync(projectId,userId,ct);
        var comment=await translations.AddCommentAsync(projectId,translationId,userId,request.Body,ct);
        return Created($"/api/projects/{projectId}/translations/{translationId}/comments",comment);
    }

    [HttpGet("{translationId:long}/history")]
    public async Task<IActionResult> History(long projectId,long translationId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await translations.HistoryAsync(projectId,translationId,ct));
    }

    [HttpPost("{translationId:long}/approve")]
    public async Task<IActionResult> Approve(long projectId,long translationId,ReviewRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireAnyAsync(projectId,userId,[ProjectRole.Manager,ProjectRole.Reviewer],ct);
        return Ok(await translations.ReviewAsync(projectId,translationId,userId,true,request.Note,request.ExpectedRowVersion,ct));
    }

    [HttpPost("{translationId:long}/reject")]
    public async Task<IActionResult> Reject(long projectId,long translationId,ReviewRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireAnyAsync(projectId,userId,[ProjectRole.Manager,ProjectRole.Reviewer],ct);
        return Ok(await translations.ReviewAsync(projectId,translationId,userId,false,request.Note,request.ExpectedRowVersion,ct));
    }
}
