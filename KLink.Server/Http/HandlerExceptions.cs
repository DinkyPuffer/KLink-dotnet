namespace KLink.Server.Http;

/// <summary>认证失败（ApiHandler.Unauthorized）。</summary>
public sealed class UnauthorizedException : Exception
{
}

/// <summary>路由未命中（ApiHandler.NotFound）。</summary>
public sealed class NotFoundException : Exception
{
}
