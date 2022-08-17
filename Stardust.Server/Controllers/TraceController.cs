﻿using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using NewLife;
using NewLife.Caching;
using NewLife.Log;
using NewLife.Serialization;
using Stardust.Data;
using Stardust.Data.Monitors;
using Stardust.Monitors;
using Stardust.Server.Common;
using Stardust.Server.Services;
using XCode;
using XCode.Membership;

namespace Stardust.Server.Controllers;

//[ApiController]
[Route("[controller]")]
public class TraceController : ControllerBase
{
    private readonly TokenService _tokenService;
    private readonly AppOnlineService _appOnline;
    private readonly UplinkService _uplink;
    private readonly Setting _setting;
    private readonly ITracer _tracer;
    private readonly ITraceStatService _stat;
    private readonly IAppDayStatService _appStat;
    private readonly ITraceItemStatService _itemStat;
    private static readonly ICache _cache = new MemoryCache();

    public TraceController(ITraceStatService stat, IAppDayStatService appStat, ITraceItemStatService itemStat, TokenService tokenService, AppOnlineService appOnline, UplinkService uplink, Setting setting, ITracer tracer)
    {
        _stat = stat;
        _appStat = appStat;
        _itemStat = itemStat;
        _tokenService = tokenService;
        _appOnline = appOnline;
        _uplink = uplink;
        _setting = setting;
        _tracer = tracer;
    }

    [ApiFilter]
    [HttpPost(nameof(Report))]
    public TraceResponse Report([FromBody] TraceModel model, String token)
    {
        var builders = model?.Builders;
        if (model == null || model.AppId.IsNullOrEmpty()) return null;

        var ip = HttpContext.GetUserHost();
        if (ip.IsNullOrEmpty()) ip = ManageProvider.UserHost;

        using var span = _tracer?.NewSpan($"traceReport-{model.AppId}", new { ip, model.ClientId, count = model.Builders?.Length, names = model.Builders?.Join(",", e => e.Name) });

        // 验证
        var (app, online) = Valid(model.AppId, model, model.ClientId, token);

        // 插入数据
        if (builders != null && builders.Length > 0) Task.Run(() => ProcessData(app, model, ip, builders));

        // 构造响应
        var rs = new TraceResponse
        {
            Period = app.Period,
            MaxSamples = app.MaxSamples,
            MaxErrors = app.MaxErrors,
            Timeout = app.Timeout,
            //Excludes = app.Excludes?.Split(",", ";"),
            MaxTagLength = app.MaxTagLength,
            EnableMeter = app.EnableMeter,
        };

        // Vip客户端。高频次大样本采样，10秒100次，逗号分割，支持*模糊匹配
        if (app.IsVip(model.ClientId))
        {
            rs.Period = 10;
            rs.MaxSamples = 100;
        }

        // 新版本才返回Excludes，老版本客户端在处理Excludes时有BUG，错误处理/
        if (!model.Version.IsNullOrEmpty()) rs.Excludes = app.Excludes?.Split(",", ";");

        return rs;
    }

    [ApiFilter]
    [HttpPost(nameof(ReportRaw))]
    public async Task<TraceResponse> ReportRaw(String token)
    {
        var req = Request;
        if (req.ContentLength <= 0) return null;

        var ms = new MemoryStream();
        if (req.ContentType == "application/x-gzip")
        {
            using var gs = new GZipStream(req.Body, CompressionMode.Decompress);
            await gs.CopyToAsync(ms);
        }
        else
        {
            await req.Body.CopyToAsync(ms);
        }

        ms.Position = 0;
        var body = ms.ToStr();
        var model = body.ToJsonEntity<TraceModel>();

        return Report(model, token);
    }

    private (AppTracer, AppOnline) Valid(String appId, TraceModel model, String clientId, String token)
    {
        var set = _setting;

        // 新版验证方式，访问令牌
        App ap = null;
        if (!token.IsNullOrEmpty() && token.Split(".").Length == 3)
        {
            var (jwt, ap1) = _tokenService.DecodeToken(token, set.TokenSecret);
            if (appId.IsNullOrEmpty()) appId = ap1?.Name;
            if (clientId.IsNullOrEmpty()) clientId = jwt.Id;

            ap = ap1;
        }

        //ap = _tokenService.Authorize(appId, null, set.AutoRegister);
        if (ap == null) ap = App.FindByName(model.AppId);

        // 新建应用配置
        var app = AppTracer.FindByName(appId);
        if (app == null) app = AppTracer.Find(AppTracer._.Name == appId);
        if (app == null)
        {
            var obj = AppTracer.Meta.Table;
            lock (obj)
            {
                app = AppTracer.FindByName(appId);
                if (app == null)
                {
                    app = new AppTracer
                    {
                        Name = model.AppId,
                        DisplayName = model.AppName,
                        //AppId = ap.Id,
                        //Enable = ap.Enable,
                    };
                    if (ap != null)
                    {
                        app.AppId = ap.Id;
                        app.Enable = ap.Enable;
                        app.Category = ap.Category;
                    }
                    else
                    {
                        app.Enable = set.AutoRegister;
                    }

                    app.Insert();
                }
            }
        }

        if (ap != null)
        {
            if (ap.DisplayName.IsNullOrEmpty() || ap.DisplayName == ap.Name) ap.DisplayName = model.AppName;

            // 双向同步应用分类
            if (!ap.Category.IsNullOrEmpty())
                app.Category = ap.Category;
            else if (!app.Category.IsNullOrEmpty())
            {
                ap.Category = app.Category;
                ap.Update();
            }

            if (app.AppId == 0) app.AppId = ap.Id;
            if (app.DisplayName.IsNullOrEmpty() || app.DisplayName == app.Name) app.DisplayName = ap.DisplayName;
            app.Update();
        }

        var ip = HttpContext.GetUserHost();
        if (clientId.IsNullOrEmpty()) clientId = ip;

        // 收集应用性能信息
        if (app.EnableMeter) App.WriteMeter(model, ip);

        // 更新心跳信息
        var online = _appOnline.UpdateOnline(ap, clientId, ip, token, model.Info);

        // 检查应用有效性
        if (!app.Enable) throw new ArgumentOutOfRangeException(nameof(appId), $"应用[{appId}]已禁用！");

        return (app, online);
    }

    private void ProcessData(AppTracer app, TraceModel model, String ip, ISpanBuilder[] builders)
    {
        // 排除项
        var excludes = app.Excludes.Split(",", ";") ?? new String[0];
        var timeoutExcludes = app.TimeoutExcludes.Split(",", ";") ?? new String[0];

        var now = DateTime.Now;
        var startTime = now.AddDays(-_setting.DataRetention);
        var endTime = now.AddDays(1);
        var traces = new List<TraceData>();
        var samples = new List<SampleData>();
        foreach (var item in builders)
        {
            // 剔除指定项
            if (item.Name.IsNullOrEmpty()) continue;
            //if (app.ID == 30 && item.Name[0] == '/') XTrace.WriteLine("TraceProcess: {0}", item.Name);
            if (excludes != null && excludes.Any(e => e.IsMatch(item.Name)))
            {
                _tracer?.NewSpan("trace-Exclude", item.Name);
                continue;
            }
            //if (item.Name.EndsWithIgnoreCase("/Trace/Report")) continue;

            // 拒收超期数据，拒收未来数据
            var timestamp = item.StartTime.ToDateTime().ToLocalTime();
            if (timestamp < startTime || timestamp > endTime)
            {
                _tracer?.NewSpan("trace-ErrorTime", $"{item.Name}-{timestamp.ToFullString()}");
                continue;
            }

            // 拒收超长项
            if (item.Name.Length > TraceData._.Name.Length)
            {
                _tracer?.NewSpan("trace-LongName", item.Name);
                continue;
            }

            // 检查跟踪项
            var ti = app.GetOrAddItem(item.Name);
            if (ti == null || !ti.Enable)
            {
                _tracer?.NewSpan("trace-ErrorItem", item.Name);
                continue;
            }

            var td = TraceData.Create(item);
            td.AppId = app.ID;
            td.ItemId = ti.Id;
            td.ClientId = model.ClientId ?? ip;
            td.CreateIP = ip;
            td.CreateTime = now;

            traces.Add(td);

            //samples.AddRange(SampleData.Create(td, item.Samples, true));
            samples.AddRange(SampleData.Create(td, item.ErrorSamples, false));

            // 超时时间。超过该时间时标记为异常，默认0表示使用应用设置，-1表示不判断超时
            var timeout = ti.Timeout;
            if (timeout == 0) timeout = app.Timeout;

            var isTimeout = timeout > 0 && !timeoutExcludes.Any(e => e.IsMatch(item.Name));
            if (item.Samples != null && item.Samples.Count > 0)
            {
                // 超时处理为异常，累加到错误数之中
                if (isTimeout) td.Errors += item.Samples.Count(e => e.EndTime - e.StartTime > timeout);

                samples.AddRange(SampleData.Create(td, item.Samples, true));
            }

            // 如果最小耗时都超过了超时设置，则全部标记为错误
            if (isTimeout && td.MinCost >= timeout && td.Errors < td.Total) td.Errors = td.Total;

            // 处理克隆。拷贝一份入库，归属新的跟踪项，但名称不变
            foreach (var elm in app.GetClones(item.Name, model.ClientId))
            {
                var td2 = td.CloneEntity(true);
                td2.Id = 0;
                td2.ItemId = elm.Id;
                td2.LinkId = td.Id;

                traces.Add(td2);
            }
        }

        // 更新XCode后，支持批量插入的自动分表，内部按照实体类所属分表进行分组插入
        traces.Insert(true);
        samples.Insert(true);

        // 更新统计
        _stat.Add(traces);
        _appStat.Add(now.Date);
        if (now.Hour == 0 && now.Minute <= 10) _appStat.Add(now.Date.AddDays(-1));
        _itemStat.Add(app.ID);

        // 发送给上联服务器
        _uplink.Report(model);
    }
}