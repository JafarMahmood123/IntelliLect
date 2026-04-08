using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

[ApiController]
public abstract class ApiBaseController : ControllerBase
{
    protected Guid UserId
    {
        get
        {
            var userIdClaim = User.FindFirst("uid")?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            {
                throw new UnauthorizedAccessException("Invalid user claims.");
            }
            return userId;
        }
    }
}