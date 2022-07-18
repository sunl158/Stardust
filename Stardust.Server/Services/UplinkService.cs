﻿using NewLife;
using NewLife.Remoting;
using NewLife.Serialization;
using Stardust.Monitors;

namespace Stardust.Server.Services;

public class UplinkService
{
    public String Server { get; set; }

    private ApiHttpClient _client;
    private String _server;

    private ApiHttpClient GetClient()
    {
        var addr = Server;
        if (addr.IsNullOrEmpty())
        {
            var set = Setting.Current;
            addr = set.UplinkServer;
        }

        if (_client != null)
        {
            if (_server == addr) return _client;
        }

        if (addr.IsNullOrEmpty()) return null;

        _client = new ApiHttpClient(addr);

        _server = addr;

        return _client;
    }

    public void Report(TraceModel model)
    {
        if (model == null) return;

        var client = GetClient();
        if (client == null) return;

        Task.Run(() =>
        {
            // 数据过大时，以压缩格式上传
            var body = model.ToJson();
            var rs = body.Length > 1024 ?
                 client.PostAsync<TraceResponse>("Trace/ReportRaw", body.GetBytes()) :
                 client.PostAsync<TraceResponse>("Trace/Report", model);
        }).ConfigureAwait(false);
    }
}