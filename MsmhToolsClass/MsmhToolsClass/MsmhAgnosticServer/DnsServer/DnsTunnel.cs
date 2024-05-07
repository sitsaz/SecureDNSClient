﻿using System.Diagnostics;
using System.Net;

namespace MsmhToolsClass.MsmhAgnosticServer;

public class DnsTunnel
{
    public static async Task Process(AgnosticResult aResult, AgnosticProgram.DnsRules dnsRulesProgram, DnsCache dnsCaches, AgnosticSettings settings, EventHandler<EventArgs>? onRequestReceived)
    {
        DnsEnums.DnsProtocol dnsProtocol = aResult.Protocol switch
        {
            RequestProtocol.UDP => DnsEnums.DnsProtocol.UDP,
            RequestProtocol.TCP => DnsEnums.DnsProtocol.TCP,
            RequestProtocol.DoH => DnsEnums.DnsProtocol.DoH,
            _ => DnsEnums.DnsProtocol.Unknown
        };
        
        // Event
        string msgReqEvent = $"[{aResult.Local_EndPoint.Address}] [{dnsProtocol}] ";

        // Create Request
        DnsMessage dmQ = DnsMessage.Read(aResult.FirstBuffer, dnsProtocol);
        if (dmQ.IsSuccess && dmQ.Header.QuestionsCount > 0 && dmQ.Questions.QuestionRecords.Count > 0)
        {
            DnsRequest dnsRequest = new(aResult.Socket, aResult.Ssl_Stream, aResult.Ssl_Kind, aResult.Local_EndPoint, aResult.Remote_EndPoint, aResult.FirstBuffer, dnsProtocol);
            string addressQ = dmQ.Questions.QuestionRecords[0].QNAME;
            DnsEnums.RRType typeQ = dmQ.Questions.QuestionRecords[0].QTYPE;

            msgReqEvent += $"Q: {addressQ}, A: ";

            bool isCached = dnsCaches.TryGet(dmQ, out DnsMessage dmR);
            bool usedCache = false;
            if (isCached)
            {
                if (dmR.IsSuccess)
                {
                    bool isTryWriteSuccess = DnsMessage.TryWrite(dmR, out byte[] responseCached);
                    //Debug.WriteLine("========== IsTryWriteSuccess: " + isTryWriteSuccess);
                    if (isTryWriteSuccess)
                    {
                        DnsMessage validate = DnsMessage.Read(responseCached, dnsRequest.Protocol);
                        if (validate.IsSuccess)
                        {
                            await dnsRequest.SendToAsync(responseCached).ConfigureAwait(false);
                            usedCache = true;
                        }
                    }
                }

                if (!usedCache)
                {
                    // TTL Expired - Remove From Cache
                    dnsCaches.TryRemove(dmQ);
                }
            }
            //Debug.WriteLine("========== Used Cache: " + usedCache);

            if (!usedCache)
            {
                // Apply DnsRules Program
                AgnosticProgram.DnsRules.DnsRulesResult drr = new();
                if (dnsRulesProgram.RulesMode != AgnosticProgram.DnsRules.Mode.Disable)
                {
                    drr = await dnsRulesProgram.GetAsync(aResult.Local_EndPoint.Address.ToString(), addressQ, settings).ConfigureAwait(false);
                }
                
                bool usedFakeOrCustom = false;
                if (drr.IsMatch)
                {
                    // Black List
                    if (drr.IsBlackList)
                    {
                        await dnsRequest.SendFailedResponseAsync().ConfigureAwait(false);
                        usedFakeOrCustom = true;

                        msgReqEvent += "Request Denied - Black List";
                        onRequestReceived?.Invoke(msgReqEvent, EventArgs.Empty);
                        return;
                    }

                    // If Custom Dns Couldn't Get An IP
                    if (string.IsNullOrEmpty(drr.Dns))
                    {
                        await dnsRequest.SendFailedResponseAsync().ConfigureAwait(false);
                        usedFakeOrCustom = true;

                        msgReqEvent += "Request Denied - Your Dns Rule Couldn't Get An IP!";
                        onRequestReceived?.Invoke(msgReqEvent, EventArgs.Empty);
                        return;
                    }

                    // Fake DNS / Dns Domain / Custom Dns Or Smart DNS
                    bool isDnsIp = NetworkTool.IsIp(drr.Dns, out IPAddress? dnsIp);
                    if (isDnsIp && dnsIp != null)
                    {
                        bool isDnsIpv6 = NetworkTool.IsIPv6(dnsIp);
                        if (isDnsIpv6)
                        {
                            // IPv6
                            if (typeQ == DnsEnums.RRType.AAAA)
                            {
                                dmR = DnsMessage.CreateResponse(dmQ, 1, 0, 0);
                                dmR.Answers.AnswerRecords.Clear();
                                dmR.Answers.AnswerRecords.Add(new AaaaRecord(addressQ, 60, dnsIp));

                                bool isTryWriteSuccess = DnsMessage.TryWrite(dmR, out byte[] aBuffer);
                                if (isTryWriteSuccess)
                                {
                                    await dnsRequest.SendToAsync(aBuffer).ConfigureAwait(false);
                                    usedFakeOrCustom = true;
                                    bool cacheSuccess = dnsCaches.TryAdd(dmQ, dmR);
                                    Debug.WriteLine("ADDED TO CACHE 1: " + cacheSuccess);
                                }
                            }
                        }
                        else
                        {
                            // IPv4
                            if (typeQ == DnsEnums.RRType.A)
                            {
                                dmR = DnsMessage.CreateResponse(dmQ, 1, 0, 0);
                                dmR.Answers.AnswerRecords.Clear();
                                dmR.Answers.AnswerRecords.Add(new ARecord(addressQ, 60, dnsIp));

                                bool isTryWriteSuccess = DnsMessage.TryWrite(dmR, out byte[] aBuffer);
                                if (isTryWriteSuccess)
                                {
                                    await dnsRequest.SendToAsync(aBuffer).ConfigureAwait(false);
                                    usedFakeOrCustom = true;
                                    bool cacheSuccess = dnsCaches.TryAdd(dmQ, dmR);
                                    Debug.WriteLine("ADDED TO CACHE 2: " + cacheSuccess);
                                }
                            }
                        }
                    }
                }

                if (!usedFakeOrCustom && settings.DNSs.Count > 0)
                {
                    byte[] response = await DnsClient.QueryAsync(dnsRequest.Buffer, dnsRequest.Protocol, settings).ConfigureAwait(false);
                    dmR = DnsMessage.Read(response, dnsRequest.Protocol);
                    if (dmR.IsSuccess)
                    {
                        await dnsRequest.SendToAsync(response).ConfigureAwait(false);
                        bool cacheSuccess = dnsCaches.TryAdd(dmQ, dmR);
                        Debug.WriteLine("ADDED TO CACHE 3: " + cacheSuccess);
                    }
                    else
                    {
                        await dnsRequest.SendFailedResponseAsync().ConfigureAwait(false);
                    }
                }
            }

            // Event
            List<string> answers = new();
            if (dmR.IsSuccess)
            {
                foreach (IResourceRecord answer in dmR.Answers.AnswerRecords)
                {
                    if (answer is ARecord aRecord) answers.Add(aRecord.IP.ToString());
                    else if (answer is AaaaRecord aaaaRecord) answers.Add(aaaaRecord.IP.ToString());
                    //else if (answer is CNameRecord cNameRecord) answers.Add(cNameRecord.CName);
                    else
                    {
                        string a = answer.TYPE.ToString();
                        if (!answers.IsContain(a))
                        {
                            answers.Add(answer.CLASS.ToString());
                            answers.Add(answer.TimeToLive.ToString());
                            answers.Add(a);
                        }
                    }
                }
            }

            if (answers.Count != 0)
            {
                msgReqEvent += answers.ToString(", ");
                onRequestReceived?.Invoke(msgReqEvent, EventArgs.Empty);
            }

            //Debug.WriteLine(dmR.ToString());
        }
    }
}