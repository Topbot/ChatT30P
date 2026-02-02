using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;

namespace Core
{
    public sealed class RuBertSentiment
    {
        private static readonly Lazy<InferenceSession> Session = new Lazy<InferenceSession>(CreateSession);
        private static readonly Lazy<TokenizerConfig> Tokenizer = new Lazy<TokenizerConfig>(LoadTokenizer);
        private static readonly object Locker = new object();

        public static string Analyze(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "neutral";
            var tokenizer = Tokenizer.Value;
            if (tokenizer == null) return "neutral";

            var tokens = Tokenize(text, tokenizer);
            var ids = BuildInputIds(tokens, tokenizer, out var attentionMask, out var tokenTypeIds);

            var inputs = new List<NamedOnnxValue>();
            inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", ids));
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask));
            if (Session.Value.InputMetadata.ContainsKey("token_type_ids"))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
            }

            using (var results = Session.Value.Run(inputs))
            {
                var logits = results.FirstOrDefault(r => r.Name == "logits")?.AsTensor<float>();
                if (logits == null || logits.Length < 3) return "neutral";
                var scores = logits.ToArray();
                var maxIndex = 0;
                var maxValue = scores[0];
                for (var i = 1; i < scores.Length; i++)
                {
                    if (scores[i] > maxValue)
                    {
                        maxValue = scores[i];
                        maxIndex = i;
                    }
                }
                switch (maxIndex)
                {
                    case 1: return "positive";
                    case 2: return "negative";
                    default: return "neutral";
                }
            }
        }

        private static InferenceSession CreateSession()
        {
            var modelPath = Path.Combine(GetAppDataPath(), "model.onnx");
            return new InferenceSession(modelPath);
        }

        private static TokenizerConfig LoadTokenizer()
        {
            var baseDir = GetAppDataPath();
            var vocabPath = Path.Combine(baseDir, "vocab.txt");
            if (!File.Exists(vocabPath)) return null;

            var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
            var lines = File.ReadAllLines(vocabPath);
            for (var i = 0; i < lines.Length; i++)
            {
                var token = lines[i];
                if (!vocab.ContainsKey(token))
                    vocab[token] = i;
            }

            var config = new TokenizerConfig
            {
                Vocab = vocab,
                DoLowerCase = false,
                UnknownToken = "[UNK]",
                SepToken = "[SEP]",
                ClsToken = "[CLS]",
                PadToken = "[PAD]",
                MaxLength = 512
            };

            var tokenizerPath = Path.Combine(baseDir, "tokenizer.json");
            if (File.Exists(tokenizerPath))
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(tokenizerPath));
                    var doLower = json.SelectToken("$.model.lowercase") ?? json.SelectToken("$.normalizer.lowercase") ?? json.SelectToken("$.do_lower_case");
                    if (doLower != null && bool.TryParse(doLower.ToString(), out var lower))
                        config.DoLowerCase = lower;
                }
                catch
                {
                }
            }

            return config;
        }

        private static DenseTensor<long> BuildInputIds(List<string> tokens, TokenizerConfig config, out DenseTensor<long> attentionMask, out DenseTensor<long> tokenTypeIds)
        {
            var maxLen = config.MaxLength;
            var ids = new long[maxLen];
            var mask = new long[maxLen];
            var types = new long[maxLen];

            var clsId = GetTokenId(config, config.ClsToken);
            var sepId = GetTokenId(config, config.SepToken);
            var padId = GetTokenId(config, config.PadToken);

            var index = 0;
            ids[index] = clsId;
            mask[index] = 1;
            index++;

            foreach (var token in tokens)
            {
                if (index >= maxLen - 1) break;
                ids[index] = GetTokenId(config, token);
                mask[index] = 1;
                index++;
            }

            if (index < maxLen)
            {
                ids[index] = sepId;
                mask[index] = 1;
                index++;
            }

            for (; index < maxLen; index++)
            {
                ids[index] = padId;
                mask[index] = 0;
                types[index] = 0;
            }

            var inputIds = new DenseTensor<long>(ids, new[] { 1, maxLen });
            attentionMask = new DenseTensor<long>(mask, new[] { 1, maxLen });
            tokenTypeIds = new DenseTensor<long>(types, new[] { 1, maxLen });
            return inputIds;
        }

        private static List<string> Tokenize(string text, TokenizerConfig config)
        {
            var normalized = config.DoLowerCase ? text.ToLowerInvariant() : text;
            var basicTokens = Regex.Split(normalized, @"[^\p{L}\p{N}]+")
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();

            var result = new List<string>();
            foreach (var token in basicTokens)
            {
                if (config.Vocab.ContainsKey(token))
                {
                    result.Add(token);
                    continue;
                }
                WordPiece(token, config, result);
            }
            return result;
        }

        private static void WordPiece(string token, TokenizerConfig config, List<string> output)
        {
            var chars = token.ToCharArray();
            var isBad = false;
            var start = 0;
            var subTokens = new List<string>();
            while (start < chars.Length)
            {
                var end = chars.Length;
                string curSub = null;
                while (start < end)
                {
                    var substr = new string(chars, start, end - start);
                    if (start > 0)
                        substr = "##" + substr;
                    if (config.Vocab.ContainsKey(substr))
                    {
                        curSub = substr;
                        break;
                    }
                    end -= 1;
                }
                if (curSub == null)
                {
                    isBad = true;
                    break;
                }
                subTokens.Add(curSub);
                start = end;
            }

            if (isBad)
            {
                output.Add(config.UnknownToken);
            }
            else
            {
                output.AddRange(subTokens);
            }
        }

        private static int GetTokenId(TokenizerConfig config, string token)
        {
            if (config.Vocab.TryGetValue(token, out var id))
                return id;
            if (config.Vocab.TryGetValue(config.UnknownToken, out var unkId))
                return unkId;
            return 0;
        }

        private static string GetAppDataPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(baseDir, "App_Data");
            return dataDir;
        }

        private class TokenizerConfig
        {
            public Dictionary<string, int> Vocab { get; set; }
            public bool DoLowerCase { get; set; }
            public string UnknownToken { get; set; }
            public string SepToken { get; set; }
            public string ClsToken { get; set; }
            public string PadToken { get; set; }
            public int MaxLength { get; set; }
        }
    }
}
