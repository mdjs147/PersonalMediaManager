using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Scan;

/// <summary>手动整理请求</summary>
/// <param name="Path">待整理的文件或目录绝对路径（不必是已配置的监控目录）</param>
public sealed record ScanPathRequest(
    [RequiredNotBlank(ErrorMessage = "路径不能为空")]
    string Path);
