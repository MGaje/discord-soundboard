﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net;

using Discord.Audio;
using Discord.Modules;

using NAudio.Wave;

namespace Discord.Soundboard
{
    public class SoundboardBot
    {
        private DiscordClient client;
        private IAudioClient audio;
        private IDictionary<string, SoundboardSpeechRecognizer> recognizers;
        private ManualResetEvent sending;

        public SoundboardBot(SoundboardBotConfiguration cfg)
        {
            Configuration = cfg ?? new SoundboardBotConfiguration();
            SoundEffectRepository = new SoundboardEffectRepository();
            SoundEffectRepository.LoadFromDirectory(Configuration.EffectsPath);

            sending = new ManualResetEvent(true);
            recognizers = new Dictionary<string, SoundboardSpeechRecognizer>();

            client = new DiscordClient(x =>
            {
                x.AppName = Configuration.Name;
                x.MessageCacheSize = 0;
                x.UsePermissionsCache = false;
                x.EnablePreUpdateEvents = true;
                x.LogLevel = LogSeverity.Info;
            });

            client.MessageReceived += OnMessageReceived;
            client.UsingModules();
            client.UsingAudio(x =>
            {
                x.Mode = AudioMode.Outgoing;
                x.EnableEncryption = false;
                x.EnableMultiserver = false;
                x.Bitrate = 128;
                x.BufferLength = 10000;
            });
        }

        public SoundboardEffectRepository SoundEffectRepository
        { get; protected set; }

        public SoundboardBotConfiguration Configuration
        { get; protected set; }

        public void Connect()
        {
            client.ExecuteAndWait(async () =>
            {
                SoundboardLoggingService.Instance.Info("connecting to server...");

                // TODO: catch exceptions and retry

                await client.Connect(Configuration.User, Configuration.Password);

                SoundboardLoggingService.Instance.Info("connected");

                if (!string.IsNullOrWhiteSpace(Configuration.Status))
                    client.SetGame(Configuration.Status);

                if (!string.IsNullOrEmpty(Configuration.VoiceChannel))
                {
                    var server = client.Servers.FirstOrDefault();
                    var voiceChannel = server.FindChannels(Configuration.VoiceChannel, ChannelType.Voice, true).FirstOrDefault();

                    if (voiceChannel != null)
                    {
                        SoundboardLoggingService.Instance.Info("connecting to voice channel...");

                        audio = await voiceChannel.JoinAudio();

                        // TODO: catch exceptions and retry
                        // TODO: log voice channel connection state
                    }
                }

                SoundboardLoggingService.Instance.Info("ready");
            });
        }

        public void PlaySoundEffect(User user, Channel ch, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            Task.Run(() =>
            {
                try
                {
                    sending.WaitOne();

                    var effect = SoundEffectRepository.FindByName(name);

                    if (audio != null && effect != null)
                    {
                        if (effect.Duration.TotalMilliseconds == 0)
                            return;

                        SoundboardLoggingService.Instance.Info(
                            string.Format("[{0}] playing '{1}'", user.Name, name));

                        SendMessage(ch, string.Format(Properties.Resources.MessagePlayingSound, name));

                        var format = new WaveFormat(48000, 16, 2);
                        var length = Convert.ToInt32(format.AverageBytesPerSecond / 60.0 * 1000.0);
                        var buffer = new byte[length];

                        using (var reader = new WaveFileReader(effect.Path))
                        using (var resampler = new WaveFormatConversionStream(format, reader))
                        {
                            int count = 0;
                            while ((count = resampler.Read(buffer, 0, length)) > 0)
                                audio.Send(buffer, 0, count);
                        }
                    }

                }
                catch (Exception ex)
                {
                    SoundboardLoggingService.Instance.Error(
                        string.Format(Properties.Resources.MessagePlayingFailed, name), ex);
                }
                finally
                {
                    sending.Set();
                }
            });

        }

        public void SendMessage(Channel channel, string text)
        {
            if (channel == null)
                return;

            channel.SendMessage(text);
        }

        protected void OnMessageReceived(object sender, MessageEventArgs e)
        {
            if (e.User.Id == client.CurrentUser.Id)
                return;

            // TODO: handle multiple attachments
            // TODO: handle exceptions

            if (e.Channel.IsPrivate && e.Message.Attachments.Length > 0)
            {
                var attachment = e.Message.Attachments.FirstOrDefault();

                if (attachment != null)
                {
                    var ext = Path.GetExtension(attachment.Filename);

                    if (attachment.Size > Configuration.MaximumSoundEffectSize)
                    {
                        SendMessage(e.Channel, Properties.Resources.MessageInvalidFileSize);
                        return;
                    }

                    if (!SoundEffectRepository.ValidateFilename(attachment.Filename))
                    {
                        SendMessage(e.Channel, Properties.Resources.MessageInvalidFilename);
                        return;
                    }

                    if (!SoundEffectRepository.ValidateFileExtension(ext))
                    {
                        SendMessage(e.Channel, Properties.Resources.MessageUnsupportedFileExtension);
                        return;
                    }

                    var key = Path.GetFileNameWithoutExtension(attachment.Filename);
                    var name = Path.GetFileName(attachment.Filename);
                    var path = Path.Combine(Configuration.EffectsPath, name);

                    if (SoundEffectRepository.Exists(name))
                    {
                        SendMessage(e.Channel, Properties.Resources.MessageSoundExists);
                        return;
                    }

                    Task.Run(() =>
                    {
                        try
                        {
                            using (var web = new WebClient())
                            {
                                SoundboardLoggingService.Instance.Info(
                                    string.Format("downloading sound '{0}'", name));

                                web.DownloadFile(attachment.Url, path);

                                SoundboardLoggingService.Instance.Info(
                                    string.Format("downloaded '{0}'", name));

                                SoundEffectRepository.Add(new SoundboardEffect(path));
                                SendMessage(e.Channel, string.Format(Properties.Resources.MessageSoundReady, name));

                                SoundboardLoggingService.Instance.Info(
                                    string.Format("sound '{0}' is ready", name));
                            }
                        }
                        catch (Exception ex)
                        {
                            SoundboardLoggingService.Instance.Error("failed to download sound '{0}'", ex);
                            SendMessage(e.Channel, string.Format(Properties.Resources.MessageDownloadFailed, name));
                        }
                    });

                }
            }

            if (e.Message.IsMentioningMe())
            {
                var tokens = e.Message.Text.Split(' ');
                var cmd = (tokens.Length >= 2) ? tokens[1].ToLowerInvariant() : string.Empty;

                SoundboardLoggingService.Instance.Info(
                    string.Format("[{0}] sent command {1}", e.User.Name, cmd));

                switch (cmd)
                {
                    case "list":
                        CommandListSounds(e.User, e.Channel);
                        break;
                    default:
                        if (SoundEffectRepository.Exists(cmd))
                            CommandPlayEffect(e.User, e.Channel, cmd);
                        else
                            CommandInvalid(e.User, e.Channel, cmd); ;
                        break;
                }
            }
        }

        protected void CommandListSounds(User user, Channel ch)
        {
            var builder = new StringBuilder();
            var list = string.Join(", ", SoundEffectRepository.Effects.Select(x => x.Key));

            SendMessage(ch, list);
        }

        protected void CommandPlayEffect(User user, Channel ch, string effect)
        {
            if (effect == null)
                return;

            PlaySoundEffect(user, ch, effect);
        }

        protected void CommandInvalid(User user, Channel ch, string command)
        {
            SendMessage(ch, string.Format(Properties.Resources.MessageInvalidCommand, command));
        }

    }
}
