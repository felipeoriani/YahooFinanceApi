﻿using CsvHelper;
using Flurl;
using Flurl.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Diagnostics;

namespace YahooFinanceApi
{
    public static partial class Yahoo
    {
        public static bool IgnoreEmptyRows = false;

        // Singleton
        static SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);
        const string QueryUrl = "https://query1.finance.yahoo.com/v7/finance/download";

        const string Period1Tag = "period1";
        const string Period2Tag = "period2";
        const string IntervalTag = "interval";
        const string EventsTag = "events";
        const string CrumbTag = "crumb";

        public static async Task<IList<Candle>> GetHistoricalAsync(string symbol, DateTime? startTime = default(DateTime?), DateTime? endTime = default(DateTime?), Period period = Period.Daily, CancellationToken token = default(CancellationToken))
		    => await GetTicksAsync(symbol, 
	                               startTime, 
	                               endTime, 
	                               period, 
	                               ShowOption.History, 
	                               r => r.ToCandle(),
                                   r => r.ToFallbackCandle(),
	                               token);

        public static async Task<IList<DividendTick>> GetDividendsAsync(string symbol, DateTime? startTime = default(DateTime?), DateTime? endTime = default(DateTime?), CancellationToken token = default(CancellationToken))
            => await GetTicksAsync(symbol, 
                                   startTime, 
                                   endTime, 
                                   Period.Daily, 
                                   ShowOption.Dividend, 
                                   r => r.ToDividendTick(), 
                                   r => r.ToFallbackDividendTick(),
                                   token);

        public static async Task<IList<SplitTick>> GetSplitsAsync(string symbol, DateTime? startTime = default(DateTime?), DateTime? endTime = default(DateTime?), CancellationToken token = default(CancellationToken))
            => await GetTicksAsync(symbol,
                                   startTime,
                                   endTime,
                                   Period.Daily,
                                   ShowOption.Split,
                                   r => r.ToSplitTick(),
                                   r => r.ToFallbackSplitTick(),
                                   token);

        static async Task<List<ITick>> GetTicksAsync<ITick>(
            string symbol,
            DateTime? startTime,
            DateTime? endTime,
            Period period,
            ShowOption showOption,
            Func<string[], ITick> instanceFunction,
            Func<string[], ITick> fallbackFunction,
            CancellationToken token
            )
        {
			using (var stream = await GetResponseStreamAsync(symbol, startTime, endTime, period, showOption.Name(), token).ConfigureAwait(false))
			using (var sr = new StreamReader(stream))
			using (var csvReader = new CsvReader(sr))
			{
                var ticks = new List<ITick>();
                while (csvReader.Read())
				{
                    string[] row = csvReader.Context.Record;
                    DateTime? dateTime = null;
                    try
                    {
                        dateTime = row[0].ToSpecDateTime();
                        ticks.Add(instanceFunction(row));
                    }
                    catch
                    {
                        if (dateTime.HasValue && !IgnoreEmptyRows)
                            ticks.Add(fallbackFunction(row));
                    }
				}
                return ticks;
            }
		}

        static async Task<Stream> GetResponseStreamAsync(string symbol, DateTime? startTime, DateTime? endTime, Period period, string events, CancellationToken token)
        {
            var (client, crumb) = await _GetClientAndCrumbAsync();

            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _GetResponseStreamAsync(client, crumb).ConfigureAwait(false);
            }
            catch (FlurlHttpException ex) when (ex.Call.Response?.StatusCode == HttpStatusCode.Unauthorized)
            {
                YahooClientFactory.Reset();
                (client, crumb) = await _GetClientAndCrumbAsync();
                return await _GetResponseStreamAsync(client, crumb).ConfigureAwait(false);
            }
            catch (FlurlHttpException ex) when (ex.Call.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                throw new Exception("You may have used an invalid ticker, or the endpoint is invalidated", ex);
            }
            finally
            {
                _semaphoreSlim.Release();
            }

            #region Local Functions

            async Task<(IFlurlClient, string)> _GetClientAndCrumbAsync()
            {
                var _client = await YahooClientFactory.GetClientAsync().ConfigureAwait(false);
                var _crumb = await YahooClientFactory.GetCrumbAsync().ConfigureAwait(false);
                return (_client, _crumb);
            }

            Task<Stream> _GetResponseStreamAsync(IFlurlClient _client, string _crumb)
            {
                var url = QueryUrl
                    .AppendPathSegment(symbol)
                    .SetQueryParam(Period1Tag, (startTime ?? new DateTime(1970, 1, 1)).ToUnixTimestamp())
                    .SetQueryParam(Period2Tag, (endTime ?? DateTime.Now).ToUnixTimestamp())
                    .SetQueryParam(IntervalTag, $"1{period.Name()}")
                    .SetQueryParam(EventsTag, events)
                    .SetQueryParam(CrumbTag, _crumb);

                //Debug.WriteLine(url);

                return url
                    .WithClient(_client)
                    .GetAsync(token)
                    .ReceiveStream();
            }

            #endregion
        }
    }
}
