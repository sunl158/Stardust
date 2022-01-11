﻿using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NewLife;
using Stardust.Data.Configs;
using Stardust.Models;
using Stardust.Server.Common;
using Stardust.Server.Models;
using Stardust.Server.Services;
using TokenService = Stardust.Server.Services.TokenService;

namespace Stardust.Server.Controllers
{
    /// <summary>配置中心服务。向应用提供配置服务</summary>
    [Route("[controller]/[action]")]
    public class ConfigController : ControllerBase
    {
        private readonly ConfigService _configService;
        private readonly TokenService _tokenService;

        public ConfigController(ConfigService configService, TokenService tokenService)
        {
            _configService = configService;
            _tokenService = tokenService;
        }

        [ApiFilter]
        public ConfigInfo GetAll(String appId, String secret, String clientId, String token, String scope, Int32 version)
        {
            if (appId.IsNullOrEmpty() && token.IsNullOrEmpty()) throw new ArgumentNullException(nameof(appId));

            // 验证
            var (app, online) = Valid(appId, secret, clientId, token);
            var ip = HttpContext.GetUserHost();

            // 作用域为空时重写
            scope = scope.IsNullOrEmpty() ? AppRule.CheckScope(app.Id, ip) : scope;

            // 作用域有改变时，也要返回配置数据
            var change = online.Scope != scope;
            online.Scope = scope;
            online.SaveAsync(3_000);

            // 版本没有变化时，不做计算处理，不返回配置数据
            if (!change && version > 0 && version >= app.Version) return new ConfigInfo { Version = app.Version, UpdateTime = app.UpdateTime };
            var dic = _configService.GetConfigs(app, scope);

            return new ConfigInfo
            {
                Version = app.Version,
                Scope = scope,
                SourceIP = ip,
                NextVersion = app.NextVersion,
                NextPublish = app.PublishTime.ToFullString(""),
                UpdateTime = app.UpdateTime,
                Configs = dic,
            };
        }

        [ApiFilter]
        [HttpPost]
        public ConfigInfo GetAll([FromBody] ConfigInModel model, String token)
        {
            if (model.AppId.IsNullOrEmpty() && token.IsNullOrEmpty()) throw new ArgumentNullException(nameof(model.AppId));

            // 验证
            var (app, online) = Valid(model.AppId, model.Secret, model.ClientId, token);
            var ip = HttpContext.GetUserHost();

            // 使用键和缺失键
            if (!model.UsedKeys.IsNullOrEmpty()) app.UsedKeys = model.UsedKeys;
            if (!model.MissedKeys.IsNullOrEmpty()) app.MissedKeys = model.MissedKeys;
            app.Update();

            // 作用域为空时重写
            var scope = model.Scope;
            scope = scope.IsNullOrEmpty() ? AppRule.CheckScope(app.Id, ip) : scope;

            // 作用域有改变时，也要返回配置数据
            var change = online.Scope != scope;
            online.Scope = scope;
            online.SaveAsync(3_000);

            // 版本没有变化时，不做计算处理，不返回配置数据
            if (!change && model.Version > 0 && model.Version >= app.Version) return new ConfigInfo { Version = app.Version, UpdateTime = app.UpdateTime };

            var dic = _configService.GetConfigs(app, scope);

            return new ConfigInfo
            {
                Version = app.Version,
                Scope = scope,
                SourceIP = ip,
                NextVersion = app.NextVersion,
                NextPublish = app.PublishTime.ToFullString(""),
                UpdateTime = app.UpdateTime,
                Configs = dic,
            };
        }

        private (AppConfig, ConfigOnline) Valid(String appId, String secret, String clientId, String token)
        {
            var set = Setting.Current;

            if (appId.IsNullOrEmpty() && !token.IsNullOrEmpty())
            {
                var ap1 = _tokenService.DecodeToken(token, set);
                appId = ap1?.Name;
            }

            var ap = _tokenService.Authorize(appId, secret, set.AutoRegister);

            // 新建应用配置
            var app = AppConfig.FindByName(appId);
            if (app == null) app = AppConfig.Find(AppConfig._.Name == appId);
            if (app == null)
            {
                var obj = AppConfig.Meta.Table;
                lock (obj)
                {
                    app = AppConfig.FindByName(appId);
                    if (app == null)
                    {
                        app = new AppConfig
                        {
                            Name = ap.Name,
                            Enable = ap.Enable,
                        };

                        app.Insert();
                    }
                }
            }

            var ip = HttpContext.GetUserHost();
            if (clientId.IsNullOrEmpty())
            {
                clientId = ip;
                if (!token.IsNullOrEmpty())
                {
                    var (jwt, _) = _tokenService.DecodeToken(token, set.TokenSecret);
                    clientId = jwt?.Id;
                }
            }

            // 更新心跳信息
            var online = ConfigOnline.UpdateOnline(app, clientId, ip, token);

            // 检查应用有效性
            if (!app.Enable) throw new ArgumentOutOfRangeException(nameof(appId), $"应用[{appId}]已禁用！");

            return (app, online);
        }

        [ApiFilter]
        [HttpPost]
        public Int32 SetAll([FromBody] SetConfigModel model, String token)
        {
            if (model.AppId.IsNullOrEmpty() && token.IsNullOrEmpty()) throw new ArgumentNullException(nameof(model.AppId));

            // 验证
            var (app, online) = Valid(model.AppId, model.Secret, model.ClientId, token);
            if (app.Readonly) throw new Exception($"应用[{app}]处于只读模式，禁止修改");

            return _configService.SetConfigs(app, model.Configs);
        }
    }
}