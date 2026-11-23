using AttendanceSystem.Helpers;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AttendanceSystem.ModelBinding;

/// <summary>
/// 日期/时间的统一模型绑定（2026-11-23）：不依赖浏览器原生 date/time 控件提交的固定格式，
/// DateOnly/TimeOnly/DateTime 都接受 2026-11-23、20261123、20:30、2030、11-23 20:30 等写法。
/// 这样即使手机浏览器/旧浏览器不执行页面脚本，手输的简写也能正确绑定，不会悄悄变成空值。
/// </summary>
public sealed class FlexibleDateTimeModelBinder : IModelBinder
{
    public enum ValueKind { Date, Time, DateTime }

    private readonly ValueKind _kind;
    private readonly bool _nullable;

    public FlexibleDateTimeModelBinder(ValueKind kind, bool nullable)
    {
        _kind = kind;
        _nullable = nullable;
    }

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (result == ValueProviderResult.None)
            return Task.CompletedTask;   // 没提交这个字段：交给默认行为（可空类型为 null）

        var raw = result.FirstValue;
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (_nullable) bindingContext.Result = ModelBindingResult.Success(null);
            else bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "请填写日期/时间");
            return Task.CompletedTask;
        }

        object? value = _kind switch
        {
            ValueKind.Date when DateTimeTextParser.TryParseDate(raw, out var date) => date,
            ValueKind.Time when DateTimeTextParser.TryParseTime(raw, out var time) => time,
            ValueKind.DateTime when DateTimeTextParser.TryParseDateTime(raw, out var dt) => dt,
            _ => null
        };

        if (value is null)
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "日期/时间格式不正确，请按 2026-11-23 或 20:30 这样填写");
            return Task.CompletedTask;
        }

        bindingContext.Result = ModelBindingResult.Success(value);
        return Task.CompletedTask;
    }
}

/// <summary>把 FlexibleDateTimeModelBinder 挂到 DateOnly / TimeOnly / DateTime（含可空）上。</summary>
public sealed class FlexibleDateTimeModelBinderProvider : IModelBinderProvider
{
    private static readonly FlexibleDateTimeModelBinder DateBinder = new(FlexibleDateTimeModelBinder.ValueKind.Date, nullable: false);
    private static readonly FlexibleDateTimeModelBinder NullableDateBinder = new(FlexibleDateTimeModelBinder.ValueKind.Date, nullable: true);
    private static readonly FlexibleDateTimeModelBinder TimeBinder = new(FlexibleDateTimeModelBinder.ValueKind.Time, nullable: false);
    private static readonly FlexibleDateTimeModelBinder NullableTimeBinder = new(FlexibleDateTimeModelBinder.ValueKind.Time, nullable: true);

    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        var type = context.Metadata.ModelType;
        if (type == typeof(DateOnly)) return DateBinder;
        if (type == typeof(DateOnly?)) return NullableDateBinder;
        if (type == typeof(TimeOnly)) return TimeBinder;
        if (type == typeof(TimeOnly?)) return NullableTimeBinder;
        // 只接管 DateOnly / TimeOnly：DateTime 仍用框架默认绑定，避免影响接口/查询里带时区的 DateTime
        return null;
    }
}
