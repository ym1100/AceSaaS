using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Acesoft.Util;

namespace Acesoft.Web.Cloud.Moji
{
	public class WeatherService : IWeatherService
	{
		private const string Api = "http://basiccity.market.alicloudapi.com";
		private const string FreeApi = "http://aliv1.data.moji.com";
		private Dictionary<string, string> headers = new Dictionary<string, string>();

        private static readonly ConcurrentDictionary<string, AqiResult> cache = new ConcurrentDictionary<string, AqiResult>();

        public string AppCode { get; private set; }
        public int RefreshMinutes { get; private set; }

        public WeatherService(string appCode, int refreshMinutes)
		{
			AppCode = appCode;
			RefreshMinutes = refreshMinutes;
			headers.Add("Authorization", "APPCODE " + appCode);
		}

		public WeaResult GetWea(string lng, string lat)
		{
			var url = $"{FreeApi}/whapi/json/aliweather/aqi";
			var postData = "lat=" + lat + "&lon=" + lng + "&token=a231972c3e7ba6b33d8ec71fd4774f5e";
            var result = HttpHelper.HttpPost(url, postData, headers, HttpHelper.ContentTypeForm);
            return SerializeHelper.FromJson(result)["data"].ToObject<WeaResult>();
		}

		public WeaResult GetWea(string cityId)
		{
            var url = $"{Api}/whapi/json/alicityweather/briefcondition";
            var postData = "cityId=" + cityId + "&token=46e13b7aab9bb77ee3358c3b672a2ae4";
            var result = HttpHelper.HttpPost(url, postData, headers, HttpHelper.ContentTypeForm);
            return SerializeHelper.FromJson(result)["data"].ToObject<WeaResult>();
		}

		private WeaAqi GetAqi(string cityId)
		{
            var url = $"{Api}/whapi/json/alicityweather/aqi";
            var postData = "cityId=" + cityId + "&token=8b36edf8e3444047812be3a59d27bab9";
            var result = HttpHelper.HttpPost(url, postData, headers, HttpHelper.ContentTypeForm);
            return SerializeHelper.FromJson(result)["data"]["aqi"].ToObject<WeaAqi>();
		}

		public AqiResult GetWeaAqi(string lng, string lat)
		{
			WeaResult wea = GetWea(lng, lat);
			return new AqiResult
			{
				city = wea.city,
				condition = wea.condition,
				aqi = GetAqi(wea.city.cityid)
			};
		}

		public AqiResult GetWeaAqi(string cityId)
		{
			WeaResult wea = GetWea(cityId);
			return new AqiResult
			{
				city = wea.city,
				condition = wea.condition,
				aqi = GetAqi(cityId),
				FetchTime = DateTime.Now
			};
		}

		public AqiResult GetCachedWeaAqi(string cityId)
		{
			return cache.AddOrUpdate(cityId, id => GetWeaAqi(id), (id, cur) =>
			{
				if (!(cur.FetchTime.AddMinutes(RefreshMinutes) > DateTime.Now))
				{
					return GetWeaAqi(id);
				}
				return cur;
			});
		}
	}
}
