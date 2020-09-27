﻿using DankDitties.Audio;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DankDitties
{
    public class VoiceChannelWorker : Worker
    {
        private readonly SocketVoiceChannel _voiceChannel;
        private readonly MetadataManager _metadataManager;
        private readonly WitAiClient _witAiClient;
        private readonly List<string> _playlist = new List<string>();
        private readonly Random _random = new Random();

        private Track _mainTrack;
        private Track _ttsTrack;

        public IEnumerable<string> Playlist => _playlist;
        public PostMetadata CurrentSong { get; private set; }

        public VoiceChannelWorker(SocketVoiceChannel voiceChannel, MetadataManager metadataManager, WitAiClient witAiClient)
        {
            _voiceChannel = voiceChannel;
            _metadataManager = metadataManager;
            _witAiClient = witAiClient;
        }

        public bool TrySkip()
        {
            if (_mainTrack.TrySkip())
            {
                Say("Ok, I am skipping this song");
                return true;
            }
            return false;
        }

        public void EnqueueSong(string songId)
        {
            // TODO: ensure song exists
            _playlist.Add(songId);
        }

        public void Say(string text)
        {
            Console.WriteLine("Enqueuing Say: " + text);
            _ttsTrack.Enqueue(new TtsClip(text));
        }

        private string _getNext()
        {
            foreach (var id in _playlist)
            {
                if (_metadataManager.HasRecord(id))
                {
                    var record = _metadataManager.GetRecord(id);
                    if (record.DownloadCacheFilename != null)
                    {
                        _playlist.Remove(id);
                        CurrentSong = record;
                        return record.DownloadCacheFilename;
                    }

                    Console.WriteLine("An item in the queue was skipped because it has not been downloaded yet");
                }
            }

            var posts = _metadataManager.Posts.ToList().Where(p => p.IsReady).ToList();
            if (posts.Count == 0)
                return null;

            var nextIndex = _random.Next(posts.Count);
            CurrentSong = posts[nextIndex];
            return posts[nextIndex].DownloadCacheFilename;
        }

        private bool _canAccessVoiceAssistant(SocketGuildUser user)
        {
            if (!Program.EnableVoiceCommands)
                return false;

            if (user.IsBot)
                return false;

            if (Program.VoiceCommandRole != null && !user.Roles.Any(r => r.Id == Program.VoiceCommandRole))
                return false;

            return true;
        }

        private async Task _assistantManager(CancellationToken cancellationToken)
        {
            var _assistants = new Dictionary<ulong, VoiceAssistantWorker>();

            IEnumerable<Task<bool>> stops;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var prevIds = _assistants.Keys.ToList();
                    var users = _voiceChannel.Users.ToDictionary(u => u.Id, u => u);
                    var newIds = users.Values
                        .Where(_canAccessVoiceAssistant)
                        .Select(u => u.Id)
                        .ToList();

                    var toStart = newIds.Except(prevIds);
                    var toStop = prevIds.Except(newIds);

                    foreach (var id in toStart)
                    {
                        var assistant = new VoiceAssistantWorker(users[id], this, _metadataManager, _witAiClient);
                        assistant.Start();
                        _assistants[id] = assistant;
                    }

                    stops = toStop.Select(id => _assistants[id].TryEnsureStoppedAsync());
                    await Task.WhenAll(stops);
                    foreach (var stop in toStop)
                    {
                        _assistants.Remove(stop);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            stops = _assistants.Values.Select(a => a.TryEnsureStoppedAsync());
            await Task.WhenAll(stops);
        }

        protected override async Task DoWorkAsync(CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine("Voice Channel Worker Connecting");
                var audioClient = await _voiceChannel.ConnectAsync();
                using var audioOutStream = audioClient.CreatePCMStream(AudioApplication.Music);

                _ = Task.Run(() => _assistantManager(cancellationToken));

                _mainTrack = new Track();
                _mainTrack.OnClipCompleted += (s, e) =>
                {
                    if (_mainTrack.Playlist.Count == 0)
                        _mainTrack.Enqueue(new FFmpegClip(_getNext()));
                };
                _mainTrack.Enqueue(new FFmpegClip(_getNext()));
                await _mainTrack.PrepareAsync();

                _ttsTrack = new Track();
                await _ttsTrack.PrepareAsync();

                await using var mainTrack = _mainTrack.SetVolume(Program.SoundVolume / 100f);
                await mainTrack.PrepareAsync();
                await using var ttsTrack = _ttsTrack.SetVolume(Program.VoiceAssistantVolume / 100f);
                await ttsTrack.PrepareAsync();
                await using var mixTrack = new MixedTrack(mainTrack, ttsTrack);
                await mixTrack.PrepareAsync();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var blockSize = 2880;
                    var buffer = new byte[blockSize];
                    var byteCount = await mixTrack.ReadAsync(buffer, 0, blockSize, cancellationToken);
                    if (byteCount == 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50));
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await audioOutStream.WriteAsync(buffer, 0, byteCount, cancellationToken);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            finally
            {
                Console.WriteLine("Voice Channel Worker Disconnecting");
                await _voiceChannel.DisconnectAsync();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await _ttsTrack.DisposeAsync();
            await _mainTrack.DisposeAsync();

            await base.DisposeAsync();
        }
    }
}