


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using iCSharp.Kernel.ScriptEngine;
using System.Web;
using iCSharp.Kernel.Helpers;

namespace iCSharp.Kernel.Shell
{
    using Common.Logging;
    using Common.Serializer;
    using iCSharp.Messages;
    using NetMQ.Sockets;

    public static class Extensions
    {
        public static bool IsBase64String(this string s)
        {
            s = s.Trim();
            return (s.Length % 4 == 0) && Regex.IsMatch(s, @"^[a-zA-Z0-9\+/]*={0,3}$", RegexOptions.None);

        }

        public static bool IsHtmlString(this string s)
        {
            s = s.Trim();
            var tagRegex = new Regex(@"<\s*([^ >]+)[^>]*>.*?<\s*/\s*\1\s*>");
            return tagRegex.IsMatch(s);
        }
    }

    public class ExecuteRequestHandler : IShellMessageHandler
    {
        private readonly ILog logger;

        private readonly IReplEngine replEngine; 

		private readonly IMessageSender messageSender;

		private int executionCount = 1;


        public ExecuteRequestHandler(ILog logger, IReplEngine replEngine, IMessageSender messageSender)
        {
            this.logger = logger;
            this.replEngine = replEngine;
			this.messageSender = messageSender;
        }

        public void HandleMessage(Message message, RouterSocket serverSocket, PublisherSocket ioPub)
        {
            this.logger.Debug(string.Format("Message Content {0}", message.Content));
            ExecuteRequest executeRequest = JsonSerializer.Deserialize<ExecuteRequest>(message.Content);

            this.logger.Info(string.Format("Execute Request received with code {0}", executeRequest.Code));

            // 1: Send Busy status on IOPub
            this.SendMessageToIOPub(message, ioPub, StatusValues.Busy);

            // 2: Send execute input on IOPub
            this.SendInputMessageToIOPub(message, ioPub, executeRequest.Code);

            // 3: Evaluate the C# code
            string code = executeRequest.Code;
            ExecutionResult results = this.replEngine.Execute(code);
            string codeOutput = this.GetCodeOutput(results);
            string codeHtmlOutput = this.GetCodeHtmlOutput(results);
            var pngOutput = GetPngOutput(results);

            Dictionary<string, object> data = new Dictionary<string, object>()
            {
                {"text/plain", codeOutput},
                {"text/html", codeHtmlOutput},
                {"image/png", pngOutput }
            };

            DisplayData displayData = new DisplayData()
            {
                Data = data,
            };

            // 4: Send execute reply to shell socket
            this.SendExecuteReplyMessage(message, serverSocket);

            // 5: Send execute result message to IOPub
            this.SendOutputMessageToIOPub(message, ioPub, displayData);

            // 6: Send IDLE status message to IOPub
            this.SendMessageToIOPub(message, ioPub, StatusValues.Idle);

            this.executionCount += 1;

        }

        private string GetCodeOutput(ExecutionResult executionResult)
        {
            StringBuilder sb = new StringBuilder();

            foreach (string result in executionResult.OutputResults.Where(x => !x.IsBase64String() && !x.IsHtmlString()))
            {
                sb.Append(result);
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private string GetCodeHtmlOutput(ExecutionResult executionResult)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Tuple<string, ConsoleColor> tuple in executionResult.OutputResultWithColorInformation.Where(x => !x.Item1.IsBase64String() && !x.Item1.IsHtmlString()))
            {
                string encoded = HttpUtility.HtmlEncode(tuple.Item1);
                sb.Append(string.Format("<font style=\"color:{0}\">{1}</font>", tuple.Item2.ToString(), encoded));
            }

            if (sb.Length > 0)
                return sb.ToString();

            // check if we have already html
            foreach (string result in executionResult.OutputResults.Where(x => !x.IsBase64String() && x.IsHtmlString()))
            {
                sb.Append(result);
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private string GetPngOutput(ExecutionResult executionResult)
        {
            StringBuilder sb = new StringBuilder();

            foreach (string result in executionResult.OutputResults.Where(x => x.IsBase64String()))
            {
                sb.Append(result);
            }

            
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public void SendMessageToIOPub(Message message, PublisherSocket ioPub, string statusValue)
        {
            Dictionary<string,string> content = new Dictionary<string, string>();
            content.Add("execution_state", statusValue);
            Message ioPubMessage = MessageBuilder.CreateMessage(MessageTypeValues.Status,
                JsonSerializer.Serialize(content), message.Header);

            this.logger.Info(string.Format("Sending message to IOPub {0}", JsonSerializer.Serialize(ioPubMessage)));
			this.messageSender.Send(ioPubMessage, ioPub);
            this.logger.Info("Message Sent");
        }

        public void SendOutputMessageToIOPub(Message message, PublisherSocket ioPub, DisplayData data)
        {
            Dictionary<string,object> content = new Dictionary<string, object>();
            content.Add("execution_count", this.executionCount);
            content.Add("data", data.Data);
            content.Add("metadata", data.MetaData);

            Message outputMessage = MessageBuilder.CreateMessage(MessageTypeValues.Output,
                JsonSerializer.Serialize(content), message.Header);

            this.logger.Info(string.Format("Sending message to IOPub {0}", JsonSerializer.Serialize(outputMessage)));
			this.messageSender.Send(outputMessage, ioPub);
        }

        public void SendInputMessageToIOPub(Message message, PublisherSocket ioPub, string code)
        {
            Dictionary<string, object> content = new Dictionary<string, object>();
            content.Add("execution_count", 1);
            content.Add("code", code);

            Message executeInputMessage = MessageBuilder.CreateMessage(MessageTypeValues.Input, JsonSerializer.Serialize(content),
                message.Header);

            this.logger.Info(string.Format("Sending message to IOPub {0}", JsonSerializer.Serialize(executeInputMessage)));
			this.messageSender.Send(executeInputMessage, ioPub);
        }

        public void SendExecuteReplyMessage(Message message, RouterSocket shellSocket)
        {
            ExecuteReplyOk executeReply = new ExecuteReplyOk()
            {
                ExecutionCount = this.executionCount,
                Payload = new List<Dictionary<string, string>>(),
                UserExpressions = new Dictionary<string, string>()
            };

            Message executeReplyMessage = MessageBuilder.CreateMessage(MessageTypeValues.ExecuteReply,
                JsonSerializer.Serialize(executeReply), message.Header);

            this.logger.Info(string.Format("Sending message to Shell {0}", JsonSerializer.Serialize(executeReplyMessage)));
			this.messageSender.Send(executeReplyMessage, shellSocket);
        }
    }
}
