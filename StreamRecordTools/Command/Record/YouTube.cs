﻿using StackExchange.Redis;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ResultType = StreamRecordTools.Program.ResultType;

namespace StreamRecordTools.Command.Record
{
    public static class YouTube
    {
        static string videoId;
        static string fileName;
        static string tempPath;
        static string outputPath;
        static string unarchivedOutputPath;
        static string memberOnlyOutputPath;
        static bool isDisableRedis;
        static DateTime streamScheduledStartTime = DateTime.MinValue;

        public static async Task<ResultType> StartRecord(string id,
            string argOutputPath,
            string argTempPath,
            string argUnArchivedOutputPath,
            string argMemberOnlyOutputPath,
            bool argIsDisableRedis = false,
            bool isDisableLiveFromStart = false,
            bool dontSendStartMessage = false)
        {
            string channelId = "", channelTitle = "";
            id = id.Replace("@", "-");
            isDisableRedis = argIsDisableRedis;

            #region 初始化
            if (!isDisableRedis)
            {
                try
                {
                    RedisConnection.Init(Utility.BotConfig.RedisOption);
                    Utility.Redis = RedisConnection.Instance.ConnectionMultiplexer;
                }
                catch (Exception ex)
                {
                    Log.Error("Redis連線錯誤，請確認伺服器是否已開啟");
                    Log.Error(ex.Message);
                    return ResultType.Error;
                }
            }

            bool isStartStream = false;
            if (id.Length == 11)
            {
                videoId = id;

                bool isError = false;
                do
                {
                    try
                    {
                        var result = await Utility.GetSnippetDataAndLiveStreamingDetailsByVideoIdAsync(videoId);
                        if (result.VideoSnippet == null)
                        {
                            Log.Error($"{videoId} 不存在直播");
                            return ResultType.Error;
                        }

                        if (result.VideoLiveStreamingDetails.ScheduledStartTimeDateTimeOffset == null)
                        {
                            Log.Error($"{videoId} 無開始直播時間");
                            return ResultType.Error;
                        }

                        channelId = result.VideoSnippet.ChannelId;
                        channelTitle = result.VideoSnippet.ChannelTitle;
                        streamScheduledStartTime = result.VideoLiveStreamingDetails.ScheduledStartTimeDateTimeOffset.Value.LocalDateTime;
                        isStartStream = result.VideoLiveStreamingDetails.ActualStartTimeDateTimeOffset.HasValue;
                        isError = false;
                    }
                    catch (HttpRequestException httpEx)
                    {
                        Log.Error(httpEx, "GetSnippetDataAndLiveStreamingDetailsByVideoIdAsync");
                        isError = true;
                        await Task.Delay(1000);
                    }
                    catch (Google.GoogleApiException apiEx) when (apiEx.HttpStatusCode == System.Net.HttpStatusCode.BadRequest && apiEx.Message.Contains("API key not valid"))
                    {
                        Log.Error(apiEx, "GetSnippetDataAndLiveStreamingDetailsByVideoIdAsync: Google API Key 錯誤");
                        return ResultType.Error;
                    }
                } while (isError);
            }
            else
            {
                Log.Error("Video Id 非 11 字元");
                return ResultType.Error;
            }

            Log.Info($"直播影片 Id: {videoId}");
            Log.Info($"頻道 Id: {channelId}");
            Log.Info($"頻道名稱: {channelTitle}");
            Log.Info($"直播預計開始時間: {streamScheduledStartTime}");
            if (!isStartStream)
            {
                do
                {
                    await Task.Delay(1000);
                    if (Utility.IsClose) return ResultType.None;
                } while (streamScheduledStartTime.AddMinutes(-1) > DateTime.Now);
            }
            else
            {
                Log.Warn($"已開台，開始錄影");
            }

            if (!argOutputPath.EndsWith(Utility.GetEnvSlash()))
                argOutputPath += Utility.GetEnvSlash();
            if (!argTempPath.EndsWith(Utility.GetEnvSlash()))
                argTempPath += Utility.GetEnvSlash();
            if (!argUnArchivedOutputPath.EndsWith(Utility.GetEnvSlash()))
                argUnArchivedOutputPath += Utility.GetEnvSlash();
            if (!argMemberOnlyOutputPath.EndsWith(Utility.GetEnvSlash()))
                argMemberOnlyOutputPath += Utility.GetEnvSlash();

            outputPath = argOutputPath.Replace("\"", "");
            tempPath = argTempPath.Replace("\"", "");
            unarchivedOutputPath = argUnArchivedOutputPath.Replace("\"", "");
            memberOnlyOutputPath = argMemberOnlyOutputPath.Replace("\"", "");
            Log.Info($"輸出路徑: {outputPath}");
            Log.Info($"暫存路徑: {tempPath}");
            Log.Info($"私人存檔路徑: {unarchivedOutputPath}");
            Log.Info($"會限存檔路徑: {memberOnlyOutputPath}");

            if (isDisableLiveFromStart)
                Log.Info("不自動從頭開始錄影");
            #endregion

            tempPath += $"{DateTime.Now:yyyyMMdd}{Utility.GetEnvSlash()}";
            if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);
            outputPath += $"{DateTime.Now:yyyyMMdd}{Utility.GetEnvSlash()}";
            if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);

            bool isCanNotRecordStream = false, isReceiveDownload = false, isNeedRestart = false;
            #region 開始錄製直播
            do
            {
                isNeedRestart = false;

                if (Utility.IsLiveEnd(videoId, true, isDisableRedis)) break;

                fileName = $"youtube_{channelId}_{DateTime.Now:yyyyMMdd_HHmmss}_{videoId}";

                CancellationTokenSource cancellationToken = new();
                CancellationToken token = cancellationToken.Token;

                // 如果關閉從開頭錄影的話，每六小時需要重新開一次錄影，才能避免掉長時間錄影導致HTTP 503錯誤
                // 但從頭開始錄影好像只能錄兩小時 :thinking:
                string arguments = "--live-from-start -f bestvideo+bestaudio ";
                if (isDisableLiveFromStart)
                    arguments = "-f b ";

                Log.Info($"存檔名稱: {fileName}");
                var process = new Process();
#if RELEASE
                process.StartInfo.FileName = "yt-dlp";
                string browser = "firefox";
#else
                process.StartInfo.FileName = "yt-dlp_min.exe";
                string browser = "chrome";
#endif

                if (Utility.InDocker)
                    arguments += "--cookies /app/cookies.txt";
                else
                    arguments += $"--cookies-from-browser {browser} --mark-watched";

                #region 如果過了一小時還沒開始錄影...
                var task = Task.Run(async () =>
                {
                    int waitTime = 3600;
                    do
                    {
                        waitTime--;
                        await Task.Delay(1000);
                        if (token.IsCancellationRequested)
                            return;
                    } while (waitTime >= 0);

                    if (!isReceiveDownload)
                    {
                        var result = await Utility.GetSnippetDataAndLiveStreamingDetailsByVideoIdAsync(videoId);
                        if (result.VideoSnippet == null)
                        {
                            Log.Warn($"{videoId} 待機所已刪除");
                            isCanNotRecordStream = true;
                            process.Kill(Signum.SIGTERM);
                            return;
                        }

                        if (streamScheduledStartTime != result.VideoLiveStreamingDetails.ScheduledStartTimeDateTimeOffset.Value.LocalDateTime)
                        {
                            streamScheduledStartTime = result.VideoLiveStreamingDetails.ScheduledStartTimeDateTimeOffset.Value.LocalDateTime;
                            Log.Info($"已更改開台時間: {streamScheduledStartTime}");
                            isNeedRestart = true;
                            process.Kill(Signum.SIGTERM);
                        }
                        else
                        {
                            Log.Warn("已等待一小時但尚未開始直播，取消錄影");
                            isCanNotRecordStream = true;
                            process.Kill(Signum.SIGTERM);
                        }
                    }
                });
                #endregion

                // --live-from-start 太吃硬碟隨機讀寫
                // --embed-metadata --embed-thumbnail 會導致不定時卡住，先移除
                // --mark-watched 疑似會導致 Cookie 出現問題，暫時移除
                process.StartInfo.Arguments = $"https://www.youtube.com/watch?v={videoId} -o \"{tempPath}{fileName}.%(ext)s\" --wait-for-video 15 {arguments}";

                // 印象中之前用ErrorDataReceived的時候能正常觸發會限訊息檢測
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.ErrorDataReceived += (sender, e) =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(e.Data))
                            return;

                        // 不顯示wait開頭的訊息避免吃爆Portainer的log
                        if (e.Data.ToLower().StartsWith("[wait]"))
                            return;

                        Log.Error(e.Data);

                        if (e.Data.Contains("members-only content") || e.Data.Contains("channel's members"))
                        {
                            Log.Error("檢測到無法讀取的會限");
                            Utility.Redis.GetSubscriber().Publish(new("youtube.startstream", RedisChannel.PatternMode.Literal), $"{videoId}:1");
                            process.Kill(Signum.SIGTERM);
                            isCanNotRecordStream = true;
                        }
                        else if (e.Data.Contains("video is private") || e.Data.Contains("Private video"))
                        {
                            Log.Error("已私人化，取消錄影");
                            process.Kill(Signum.SIGTERM);
                            isCanNotRecordStream = true;
                        }
                        else if (e.Data.Contains("video has been removed") || e.Data.Contains("removed by the uploader"))
                        {
                            Log.Error("已移除，取消錄影");
                            process.Kill(Signum.SIGTERM);
                            isCanNotRecordStream = true;
                        }
                    }
                    catch { }
                };

                process.OutputDataReceived += (sender, e) =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(e.Data))
                            return;

                        // 不顯示wait開頭的訊息避免吃爆Portainer的log
                        if (e.Data.ToLower().StartsWith("[wait]"))
                            return;

                        Log.YouTubeInfo(e.Data);

                        // 應該能用這個來判定開始直播
                        if (e.Data.ToLower().StartsWith("[download]"))
                        {
                            if (isReceiveDownload)
                                return;

                            isReceiveDownload = true;
                            Log.Info("開始直播!");

                            if (!isDisableRedis)
                            {
                                if (!dontSendStartMessage) Utility.Redis.GetSubscriber().Publish(new("youtube.startstream", RedisChannel.PatternMode.Literal), $"{videoId}:0");
                                Utility.Redis.GetDatabase().SetAdd("youtube.nowRecord", videoId);

                                if (isDisableLiveFromStart)
                                {
                                    #region 每六小時重新執行錄影
                                    int waitTime = 5 * 60 * 60 + 59 * 60;
                                    var task = Task.Run(async () =>
                                    {
                                        do
                                        {
                                            waitTime--;
                                            await Task.Delay(1000);
                                            if (token.IsCancellationRequested)
                                                return;
                                        } while (waitTime >= 0);

                                        Utility.Redis.GetSubscriber().Publish(new("youtube.rerecord", RedisChannel.PatternMode.Literal), videoId);
                                    });
                                    #endregion
                                }
                            }
                        }
                    }
                    catch { }
                };

                Log.Info(process.StartInfo.Arguments);

                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                process.WaitForExit();
                process.CancelErrorRead();
                process.CancelOutputRead();

                Utility.IsClose = true;
                Log.Info($"錄影結束");
                cancellationToken.Cancel();

                if (isNeedRestart)
                {
                    Utility.IsClose = false;

                    do
                    {
                        await Task.Delay(1000);
                        if (Utility.IsClose) break;
                    } while (streamScheduledStartTime.AddMinutes(-1) > DateTime.Now);
                }
            } while (!Utility.IsClose);
            #endregion

            #region 直播結束後的保存處理
            if (!isCanNotRecordStream) // 如果該直播沒被判定成不能錄影的會限直播的話
            {
                if (!string.IsNullOrEmpty(fileName) && Utility.IsMemberOnly(videoId)) // 如果是會限直播保存到memberOnlyOutputPath
                {
                    Log.Info($"已轉會限影片，移動資料");
                    MoveVideo(memberOnlyOutputPath, "youtube.memberonly");
                }
                else if (!string.IsNullOrEmpty(fileName) && Utility.IsDelLive) // 如果被刪檔就保存到unarchivedOutputPath
                {
                    Log.Info($"已刪檔直播，移動資料");
                    MoveVideo(unarchivedOutputPath, "youtube.unarchived");
                }
                else if (Path.GetDirectoryName(outputPath) != Path.GetDirectoryName(tempPath)) // 否則就保存到outputPath
                {
                    Log.Info("將直播轉移至保存點");
                    MoveVideo(outputPath, "youtube.endstream");

                    if (Utility.IsLiveEnd(videoId, false, isDisableRedis))
                    {
                        // https://social.msdn.microsoft.com/Forums/en-US/c2c12a9f-dc4c-4c9a-b652-65374ef999d8/get-docker-container-id-in-code?forum=aspdotnetcore
                        if (Utility.InDocker && !isDisableRedis)
                            Utility.Redis.GetSubscriber().Publish(new("streamTools.removeById", RedisChannel.PatternMode.Literal), Environment.MachineName);
                    }
                    else
                    {
                        Log.Warn("還沒關台，保留容器以供紀錄檢查");
                    }
                }
            }
            else
            {
                return ResultType.Error;
            }
            #endregion

            if (!isDisableRedis)
                await Utility.Redis.GetDatabase().SetRemoveAsync("youtube.nowRecord", videoId);

            return ResultType.Once;
        }

        private static void MoveVideo(string outputPath, string redisChannel = "")
        {
            foreach (var item in Directory.GetFiles(tempPath, $"*{videoId}.???"))
            {
                try
                {
                    Log.Info($"移動 \"{item}\" 至 \"{outputPath}{Path.GetFileName(item)}\"");
                    File.Move(item, $"{outputPath}{Path.GetFileName(item)}");
                    if (!isDisableRedis && !string.IsNullOrEmpty(redisChannel))
                        Utility.Redis.GetSubscriber().Publish(new(redisChannel, RedisChannel.PatternMode.Literal), videoId);
                }
                catch (Exception ex)
                {
                    if (Utility.InDocker) Log.Error(ex.ToString());
                    else File.AppendAllText($"{tempPath}{fileName}_err.txt", ex.ToString());
                }
            }
        }
    }
}