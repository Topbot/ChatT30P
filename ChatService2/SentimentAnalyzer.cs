using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatService2
{
    public sealed class SentimentAnalyzer : IDisposable
    {
        private readonly LLamaWeights _weights;
        private readonly StatelessExecutor _executor;
        private readonly object _sync = new();

        private SentimentAnalyzer(LLamaWeights weights, StatelessExecutor executor)
        {
            _weights = weights;
            _executor = executor;
        }

        public static SentimentAnalyzer? TryCreate(string modelPath, ILogger logger)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                {
                    logger.LogWarning("Llama model not found at {Path}", modelPath);
                    return null;
                }

                Environment.SetEnvironmentVariable("LLAMA_SET_ROWS", "1");
                try { NativeLogConfig.llama_log_set(NullLogger.Instance); } catch { }

                var modelParams = new ModelParams(modelPath)
                {
                    ContextSize = 1024,
                    GpuLayerCount = 0
                };

                var weights = LLamaWeights.LoadFromFile(modelParams);
                var executor = new StatelessExecutor(weights, modelParams, logger);
                return new SentimentAnalyzer(weights, executor);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize Llama sentiment analyzer");
                return null;
            }
        }

        public string Predict(string text, string replyText)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "unknown";

            try
            {
                lock (_sync)
                {
                    var response = Infer(text, replyText);
                    return NormalizeResponse(response);
                }
            }
            catch
            {
                return "unknown";
            }
        }

        public void Dispose()
        {
            _weights.Dispose();
            if (_executor is IDisposable disposable)
                disposable.Dispose();
        }

        private string Infer(string text, string replyText)
        {
            var trimmedReply = string.IsNullOrWhiteSpace(replyText) ? null : replyText.Trim();
            var prompt = string.IsNullOrWhiteSpace(trimmedReply)
                ? $"Определи тональность комментария. Ответь одним словом: positive, neutral, negative, unknown.\nКомментарий: {text}\nОтвет:"
                : $"Определи тональность комментария. Ответь одним словом: positive, neutral, negative, unknown.\nКомментарий: {text}\nВ ответ на сообщение: {trimmedReply}\nОтвет:";
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 6,
                AntiPrompts = new[] { "\n" }
            };
            var output = new StringBuilder();
            var task = Task.Run(async () =>
            {
                await foreach (var token in _executor.InferAsync(prompt, inferenceParams))
                    output.Append(token);
            });
            task.GetAwaiter().GetResult();
            return output.ToString();
        }

        private static string NormalizeResponse(string response)
        {
            var value = (response ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Contains("positive") || value.Contains("позитив"))
                return "positive";
            if (value.Contains("negative") || value.Contains("негатив"))
                return "negative";
            if (value.Contains("neutral") || value.Contains("нейтр"))
                return "neutral";
            if (value.Contains("unknown") || value.Contains("непонят"))
                return "unknown";
            return "unknown";
        }
    }
}
