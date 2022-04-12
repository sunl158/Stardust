﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;
using System.Xml.Serialization;
using NewLife;
using NewLife.Data;
using NewLife.Log;
using NewLife.Reflection;
using Stardust.Data.Nodes;
using Stardust.Models;
using XCode;
using XCode.Membership;

namespace Stardust.Data
{
    /// <summary>应用在线。一个应用有多个部署，每个在线会话对应一个服务地址</summary>
    public partial class AppOnline : Entity<AppOnline>
    {
        #region 对象操作
        static AppOnline()
        {
            var df = Meta.Factory.AdditionalFields;
            df.Add(__.PingCount);

            // 过滤器 UserModule、TimeModule、IPModule
            Meta.Modules.Add<TimeModule>();
            Meta.Modules.Add<IPModule>();

            // 单对象缓存
            var sc = Meta.SingleCache;
            sc.FindSlaveKeyMethod = k => Find(_.Client == k);
            sc.GetSlaveKeyMethod = e => e.Client;
        }

        /// <summary>验证数据，通过抛出异常的方式提示验证失败。</summary>
        /// <param name="isNew">是否插入</param>
        public override void Valid(Boolean isNew)
        {
            // 如果没有脏数据，则不需要进行任何处理
            if (!HasDirty) return;

            if (!Version.IsNullOrEmpty() && !Dirtys[nameof(Compile)])
            {
                var dt = AssemblyX.GetCompileTime(Version);
                if (dt.Year > 2000) Compile = dt;
            }

            if (TraceId.IsNullOrEmpty()) TraceId = DefaultSpan.Current?.TraceId;
        }
        #endregion

        #region 扩展属性
        /// <summary>应用</summary>
        [XmlIgnore, ScriptIgnore, IgnoreDataMember]
        public App App => Extends.Get(nameof(App), k => App.FindById(AppId));

        /// <summary>应用</summary>
        [Map(__.AppId, typeof(App), "Id")]
        public String AppName => App?.Name;

        /// <summary>节点</summary>
        [XmlIgnore, ScriptIgnore, IgnoreDataMember]
        public Node Node => Extends.Get(nameof(Node), k => Node.FindByID(NodeId));

        /// <summary>节点</summary>
        [Map(__.NodeId)]
        public String NodeName => Node?.Name;
        #endregion

        #region 扩展查询
        /// <summary>根据编号查找</summary>
        /// <param name="id">编号</param>
        /// <returns>实体对象</returns>
        public static AppOnline FindById(Int32 id)
        {
            if (id <= 0) return null;

            // 实体缓存
            if (Meta.Session.Count < 1000) return Meta.Cache.Find(e => e.Id == id);

            // 单对象缓存
            return Meta.SingleCache[id];

            //return Find(_.ID == id);
        }

        /// <summary>根据会话查找</summary>
        /// <param name="client">会话</param>
        /// <param name="cache">是否走缓存</param>
        /// <returns></returns>
        public static AppOnline FindByClient(String client, Boolean cache = true)
        {
            if (client.IsNullOrEmpty()) return null;

            if (!cache) return Find(_.Client == client);

            return Meta.SingleCache.GetItemWithSlaveKey(client) as AppOnline;
        }

        /// <summary>根据令牌查找</summary>
        /// <param name="token">令牌</param>
        /// <returns>实体对象</returns>
        public static AppOnline FindByToken(String token)
        {
            if (token.IsNullOrEmpty()) return null;

            // 实体缓存
            if (Meta.Session.Count < 1000) return Meta.Cache.Find(e => e.Token == token);

            return Find(_.Token == token);
        }

        /// <summary>根据应用查找所有在线记录</summary>
        /// <param name="appId"></param>
        /// <returns></returns>
        public static IList<AppOnline> FindAllByApp(Int32 appId)
        {
            //if (Meta.Session.Count < 1000) return Meta.Cache.FindAll(e => e.AppId == appId);

            return FindAll(_.AppId == appId);
        }
        #endregion

        #region 高级查询
        /// <summary>高级搜索</summary>
        /// <param name="appId"></param>
        /// <param name="nodeId"></param>
        /// <param name="category"></param>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <param name="key"></param>
        /// <param name="page"></param>
        /// <returns></returns>
        public static IList<AppOnline> Search(Int32 appId, Int32 nodeId, String category, DateTime start, DateTime end, String key, PageParameter page)
        {
            var exp = new WhereExpression();

            if (appId >= 0) exp &= _.AppId == appId;
            if (nodeId >= 0) exp &= _.NodeId == nodeId;
            if (!category.IsNullOrEmpty()) exp &= _.Category == category;
            exp &= _.UpdateTime.Between(start, end);
            if (!key.IsNullOrEmpty()) exp &= _.Name.Contains(key) | _.Client.Contains(key) | _.Version.Contains(key) | _.ProcessName.Contains(key);

            return FindAll(exp, page);
        }
        #endregion

        #region 业务操作
        /// <summary>获取 或 创建 会话</summary>
        /// <param name="client"></param>
        /// <returns></returns>
        public static AppOnline GetOrAddClient(String client)
        {
            if (client.IsNullOrEmpty()) return null;

            return GetOrAdd(client, FindByClient, k => new AppOnline { Client = k, Creator = Environment.MachineName });
        }

        /// <summary>
        /// 获取 或 创建  会话
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public static AppOnline GetOrAddClient(String ip, String token)
        {
            var key = token.GetBytes().Crc16().GetBytes().ToHex();
            var client = $"{ip}#{key}";
            var online = FindByClient(client);
            if (online == null) online = FindByToken(token);
            if (online != null) return online;

            return GetOrAddClient(client);
        }

        /// <summary>
        /// 更新在线状态
        /// </summary>
        /// <param name="app"></param>
        /// <param name="clientId"></param>
        /// <param name="ip"></param>
        /// <param name="token"></param>
        /// <param name="info"></param>
        /// <returns></returns>
        public static AppOnline UpdateOnline(App app, String clientId, String ip, String token, AppInfo info = null)
        {
            // 首先根据ClientId和Token直接查找应用在线
            var online = FindByClient(clientId) ?? FindByToken(token);
            if (online == null) online = GetOrAddClient(clientId) ?? GetOrAddClient(ip, token);
            if (clientId.IsNullOrEmpty()) online.Client = clientId;
            if (token.IsNullOrEmpty()) online.Token = token;
            online.PingCount++;
            if (online.CreateIP.IsNullOrEmpty()) online.CreateIP = ip;

            // 更新跟踪标识
            var traceId = DefaultSpan.Current?.TraceId;
            if (!traceId.IsNullOrEmpty()) online.TraceId = traceId;

            // 本地IP
            if (!clientId.IsNullOrEmpty())
            {
                var p = clientId.IndexOf('@');
                if (p > 0) online.IP = clientId[..p];
            }

            // 关联节点
            if (online.NodeId == 0)
            {
                var node = Node.FindAllByIPs(online.IP).FirstOrDefault();
                if (node != null) online.NodeId = node.ID;
            }

            online.Fill(app, info);

            online.SaveAsync();

            return online;
        }

        /// <summary>更新信息</summary>
        /// <param name="app"></param>
        /// <param name="info"></param>
        public void Fill(App app, AppInfo info)
        {
            if (app != null)
            {
                AppId = app.Id;
                Name = app.ToString();
                Category = app.Category;
                if (info != null && !info.Version.IsNullOrEmpty()) app.Version = info.Version;
            }

            if (info != null)
            {
                //Name = info.MachineName;
                Version = info.Version;
                UserName = info.UserName;
                MachineName = info.MachineName;
                ProcessId = info.Id;
                ProcessName = info.Name;
                StartTime = info.StartTime;
            }
        }

        /// <summary>删除过期，指定过期时间</summary>
        /// <param name="expire">超时时间，秒</param>
        /// <returns></returns>
        public static IList<AppOnline> ClearExpire(TimeSpan expire)
        {
            if (Meta.Count == 0) return null;

            // 10分钟不活跃将会被删除
            var exp = _.UpdateTime < DateTime.Now.Subtract(expire);
            var list = FindAll(exp, null, null, 0, 0);
            list.Delete();

            return list;
        }
        #endregion
    }
}