using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Threading.Tasks;
using System.Linq;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NPOI.XSSF.UserModel;
using Acesoft.Data;
using Acesoft.Rbac;
using Acesoft.Web.IoT.Client;
using Acesoft.Web.Mvc;
using Acesoft.Web.Cloud;
using Acesoft.Rbac.Entity;
using Acesoft.Web.IoT.Entity;

namespace Acesoft.Web.IoT.Controllers
{
	[ApiExplorerSettings(GroupName = "IoT")]
	[Route("api/[controller]/[action]")]
	[Consumes("application/json", new string[] { "multipart/form-data" })]
	public class IotController : ApiControllerBase
	{
		private readonly ILogger<IotController> logger;
        private readonly ICloudService cloudService;
        private readonly IIotService iotService;
        private readonly ICacheService cacheService;
        private readonly IUserService userService;
        private readonly IIotWsClient wsClient;
        private readonly IIotClient client;

        public IotController(ILogger<IotController> logger,
            ICloudService cloudService,
            IIotService iotService,
            ICacheService cacheService,
            IUserService userService,
            IIotWsClient wsClient,
            IIotClient client)
		{
			this.logger = logger;
            this.cloudService = cloudService;
            this.iotService = iotService;
            this.cacheService = cacheService;
            this.userService = userService;
            this.client = client;
            this.wsClient = wsClient;
		}

        #region crud
        [HttpGet, MultiAuthorize, DataSource, Action("查询设备")]
        public IActionResult List()
        {
            CheckDataSourceParameter();

            var ctx = new RequestContext(SqlScope, SqlId)
                .SetExtraParam(AppCtx.AC.Params);
            var res = AppCtx.Session.Query(ctx);

            // set online state from cache.
            foreach (var item in res)
            {
                item.value = iotService.GetData((string)item.mac, true);
            }

            return Json(res);
        }

        [HttpGet, MultiAuthorize, DataSource, Action("查询设备")]
		public IActionResult Grid([FromQuery] GridRequest request)
		{
			CheckDataSourceParameter();

			var ctx = new RequestContext(SqlScope, SqlId)
                .SetCmdType(CmdType.query)
                .SetParam(request)
                .SetExtraParam(AppCtx.AC.Params);
			var res = AppCtx.Session.QueryPageTable(ctx, request);

            // set online state from cache.
            foreach (DataRow row in res.Data.Rows)
            {
                row["online"] = iotService.GetData((string)row["mac"], false).Online;
            }

            return Json(res);
		}

        [HttpPost, MultiAuthorize, DataSource, Action("注册设备")]
        public async Task<IActionResult> Post([FromBody]JObject data)
        {
            CheckDataSourceParameter();

            var param = data.ToDictionary();
            var appInstanceId = param.GetValue("id", App.IdWorker.NextId());
            param["id"] = appInstanceId;
            var ctx = new RequestContext(SqlScope, SqlId)
                .SetCmdType(CmdType.insert)
                .SetParam(param);
            if (await AppCtx.Session.ExecuteAsync(ctx) > 0)
            {
                // update cache for device.
                var mac = data.GetValue<string>("mac");
                if (mac.HasValue())
                {
                    cacheService.RemoveDevice(mac);
                }
            }
            return Ok(null);
        }

        [HttpPut, MultiAuthorize, DataSource, Action("修改设备")]
        public async Task<IActionResult> Put([FromBody]JObject data)
        {
            CheckDataSourceParameter();

            var ctx = new RequestContext(SqlScope, SqlId)
                .SetCmdType(CmdType.update)
                .SetParam(data.ToDictionary());
            if (await AppCtx.Session.ExecuteAsync(ctx) > 0)
            {
                // update cache for device.
                var mac = data.GetValue<string>("mac");
                if (mac.HasValue())
                {
                    cacheService.RemoveDevice(mac);
                }
            }
            return Ok(null);
        }

        [HttpDelete, MultiAuthorize, DataSource, Action("删除设备")]
        public async Task<IActionResult> Delete(string id)
        {
            CheckDataSourceParameter();

            foreach (var sbno in id.Split(','))
            {
                var mac = cacheService.GetMac(sbno);

                // 按照mac删除
                var ctx = new RequestContext(SqlScope, SqlId)
                    .SetCmdType(CmdType.delete)
                    .SetParam(new
                    {
                        id = mac
                    });

                // 删除成功
                if (await AppCtx.Session.ExecuteAsync(ctx) > 0)
                {
                    cacheService.RemoveData(mac);
                    cacheService.RemoveDevice(mac);
                    cacheService.RemoveMac(sbno);
                }
            }

            return Ok(null);
        }
        #endregion

        #region cache
        [HttpGet, MultiAuthorize, DataSource, Action("刷新缓存")]
        public IActionResult RefreshParam(string cpno)
        {
            cacheService.RemoveParams(cpno);
            return Ok(null);
        }

        [HttpGet, MultiAuthorize, DataSource, Action("刷新缓存")]
        public IActionResult RefreshCmd(string cpno)
        {
            cacheService.RemoveCmds(cpno);
            return Ok(null);
        }

        [HttpGet, MultiAuthorize, DataSource, Action("刷新缓存")]
        public IActionResult RefreshDevice(string macs)
        {
            macs.Split(',').Each(mac => cacheService.RemoveDevice(mac));
            return Ok(null);
        }

        [HttpGet, MultiAuthorize, DataSource, Action("刷新缓存")]
        public IActionResult RefreshData(string macs)
        {
            macs.Split(',').Each(mac => cacheService.RemoveData(mac));
            return Ok(null);
        }
        #endregion

        #region upload
        [HttpPost, MultiAuthorize, Action("MAC上传")]
        public IActionResult Upload(IFormCollection form)
		{
			int count = 0, macs = 0, success = 0;
            var file = form.Files["file"];
            var proId = App.GetForm("productid", "");
            var sbno = App.GetForm("sbno", "");
            var cpxh = App.GetForm("cpxh", "");

            using (var stream = file.OpenReadStream())
            {
                var book = new XSSFWorkbook(stream);
                var sheet = book.GetSheetAt(0);
                var maxRow = sheet.LastRowNum;

                for (int row = 1; row <= maxRow; row++)
                {
                    count++;

                    var mac = sheet.GetRow(row).GetCell(0).StringCellValue.Trim();
                    if (mac.Length == 12)
                    {
                        macs++;

                        var ctx = new RequestContext("iot", "device")
                        .SetCmdType(CmdType.insert)
                        .SetParam(new
                        {
                            id = App.IdWorker.NextId(),
                            product_id = proId,
                            user_id = AppCtx.AC.User.Id,
                            sbno,
                            mac,
                            cpxh,
                            dregist = DateTime.Now.ToDateStr(),
                            state = "0"
                        });

                        success += AppCtx.Session.Execute(ctx);
                    }
                }
            }

            return Ok(new
			{
				count,
				macs,
				success
			});
        }
        #endregion

        #region execcmd
        [HttpGet, MultiAuthorize, Action("下发操作")]
		public async Task<IActionResult> ExecCmd(IotRequest request)
		{
            var opt = new IoT_OptLog
            {
                DCreate = request.DCreate,
                Mac = request.Mac,
                Cmd = request.Cmd,
                Body = request.Body,
                User_Id = AppCtx.AC.User.Id
            };
            opt.InitializeId();

            try
            {
                // check opt
                var device = cacheService.GetDevice(request.Mac);
                opt.Sbno = device.Sbno;

                Check.Require(device.Cmds.ContainsKey(request.Cmd), $"该设备({device.Cpno})不支持操作：{request.Cmd}");
                var cmd = device.Cmds[request.Cmd];
                opt.Name = cmd.Name;

                var ctx = await client.Send(request);
                var result = ctx.Response.Body;

                switch (result.Substring(0, 2))
                {
                    case "00":
                        opt.Result = ctx.Response.Body;
                        opt.Success = true;
                        if (result.Length > 2)
                        {
                            return Ok(result.Substring(2));
                        }
                        return Ok(null);

                    case "01":
                        throw new AceException("校验错误");

                    case "02":
                        throw new AceException("会话错误");

                    case "03":
                        throw new AceException("非法设备");

                    default:
                        throw new AceException(result.Substring(2));
                }
            }
            catch (Exception ex)
            {
                opt.Result = ex.GetMessage();
                throw;
            }
            finally
            {
                opt.DUpdate = DateTime.Now;
                iotService.LogOpt(opt);
            }
        }
        #endregion

        #region getdata
        [HttpGet, MultiAuthorize, Action("获取状态")]
		public IActionResult GetData(string mac)
		{
            var data = iotService.GetData(mac, true);
			return Ok(data);
		}

        [HttpGet, MultiAuthorize, Action("获取状态")]
        public IActionResult GetDataById(string sbno)
        {
            return GetData(cacheService.GetMac(sbno));
        }
        #endregion

        #region auth
        [HttpPost, MultiAuthorize, Action("添加授权")]
        public IActionResult PostAuth([FromBody]JObject data)
        {
            iotService.PostAuth(new
            {
                newid = App.IdWorker.NextId(),
                ownerid = AppCtx.AC.User.Id,
                userid = GetUser(data).Id,
                mac = data.GetValue("mac", ""),
                sbno = data.GetValue("sbno", ""),
                alias = data.GetValue("alias", ""),
                write = data.GetValue("write", true)
            });

            return Ok(null);
        }

        [HttpPut, MultiAuthorize, Action("修改授权")]
        public IActionResult PutAuth([FromBody] JObject data)
        {
            iotService.PutAuth(data.ToDictionary());

            return Ok(null);
        }

        [HttpDelete, MultiAuthorize, Action("删除授权")]
        public IActionResult DeleteAuth(long id)
        {
            iotService.DeleteAuth(id);

            return Ok(null);
        }
        #endregion

        #region bind
        [HttpPost, MultiAuthorize, Action("绑定设备")]
		public IActionResult PostBind([FromBody]JObject data)
		{
            var mac = data.GetValue("mac", "");
            var sbno = data.GetValue("sbno", "");
            Check.Require(mac.HasValue(), $"必须指定MAC地址");

            var loc = new Location
            {
                Longitude = data.GetValue("longitude", ""),
                Latitude = data.GetValue("latitude", "")
            };
            if (data.ContainsKey("address"))
            {
                loc.AdCode = data.GetValue("adcode", "");
                loc.Address = data.GetValue("address", "");
                loc.Province = data.GetValue("province", "");
                loc.City = data.GetValue("city", "");
                loc.District = data.GetValue("district", "");
            }
            else
            {
                loc = cloudService.GetLocationService().GetLoc(loc.Longitude, loc.Latitude);
            }

			var wea = cloudService.GetWeatherService().GetWea(loc.Longitude, loc.Latitude);
            iotService.PostBind(new
            {
                mac,
                sbno,
                newid = App.IdWorker.NextId(),
                userid = AppCtx.AC.User.Id,
                loc.Longitude,
                loc.Latitude,
                loc.Address,
                loc.Province,
                loc.City,
                loc.District,
                adcode = loc.AdCode,
                wea.city.cityid
            });

            // refresh cache for device.
            cacheService.RemoveDevice(mac);

			return Ok(null);
        }

        [HttpDelete, MultiAuthorize, Action("解除绑定")]
        public IActionResult Unbind(string mac, string sbno, long? userId)
        {
            iotService.DeleteBind(new
            {
                mac,
                sbno,
                userid = userId ?? AppCtx.AC.User.Id
            });

            // refresh cache for device.
            cacheService.RemoveDevice(mac);

            return Ok(null);
        }
        #endregion

        #region owner
        [HttpPut, MultiAuthorize, Action("修改拥有者")]
        public IActionResult PutOwner([FromBody]JObject data)
        {
            var mac = data.GetValue("mac", "");
            Check.Require(mac.HasValue(), $"必须指定MAC地址");
            var sbno = data.GetValue("sbno", "");
            Check.Require(sbno.HasValue(), $"必须指定设备ID");

            var device = iotService.GetDevice(mac, AppCtx.AC.User.Id);
            iotService.PutOwner(new
            {
                mac,
                sbno,
                ownerid = AppCtx.AC.User.Id,
                userid = GetUser(data).Id
            });

            // refresh cache for device.
            cacheService.RemoveDevice(mac);

            return Ok(null);
        }

        private Rbac_User GetUser(JObject data)
        {
            var mobile = data.GetValue("mobile", "");
            Check.Require(mobile.HasValue(), $"必须指定接收者手机号");
            var receiver = userService.GetByMobile(mobile);
            Check.Require(receiver != null, $"指定手机号未注册为用户");

            return receiver;
        }
        #endregion

        #region reset
        [HttpDelete, MultiAuthorize, Action("恢复出厂")]
        public IActionResult Reset(string id)
        {
            foreach (var mac in id.Split(','))
            {
                iotService.Reset(mac);
                cacheService.RemoveData(mac);
                cacheService.RemoveDevice(mac);
            }

            return Ok(null);
        }
        #endregion

        #region setstatus
        [HttpGet, MultiAuthorize, Action("设置缓存")]
        public IActionResult SetStatus(string key, string value)
        {
            cacheService.Set(key, value.ToObject<bool>());
            return Ok(null);
        }
        #endregion

        #region iotclient
        public IActionResult StartClient()
        {
            client.Open();
            return Ok(null);
        }

        public IActionResult StopClient()
        {
            client.Close();
            return Ok(null);
        }
        #endregion

        #region wsclient
        [HttpGet, MultiAuthorize, Action("启动服务")]
		public IActionResult StartServer(string server)
		{
			wsClient.Start(server);
			return Ok(null);
		}

        [HttpGet, MultiAuthorize, Action("停止服务")]
		public IActionResult StopServer(string server)
		{
			wsClient.Stop(server);
			return Ok(null);
		}

        [HttpGet, MultiAuthorize, Action("重启服务")]
		public IActionResult RestartServer(string server)
		{
			wsClient.Restart(server);
			return Ok(null);
		}
        #endregion
    }
}