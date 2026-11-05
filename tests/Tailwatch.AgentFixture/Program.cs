using System.Net;
using System.Net.Sockets;
using System.Text;

var port=int.Parse(args[0]);var mode=args[1];
if(mode=="exit")return 45;
if(mode=="waiting"){await Task.Delay(Timeout.Infinite);return 0;}
var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();
while(true)
{
    var client=await listener.AcceptTcpClientAsync();
    _=Task.Run(async()=>
    {
        using(client)
        {
            try
            {
                using var reader=new StreamReader(client.GetStream(),leaveOpen:true);
                var first=await reader.ReadLineAsync();if(first==null)return;
                while(await reader.ReadLineAsync() is {Length:>0}){}
                if(mode=="hang"||(mode=="snapshot-hang"&&first.Contains("/v1/snapshot")))
                {
                    if(args.Length>2)File.WriteAllText(args[2],"pending");
                    await Task.Delay(Timeout.Infinite);return;
                }
                if(mode=="health-body-hang")
                {
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 64\r\nContent-Type: text/plain\r\n\r\n"));
                    await Task.Delay(Timeout.Infinite);return;
                }
                var status=mode is "unavailable" or "sampler-failed" or "health-oversized"?"503 Service Unavailable":"200 OK";
                var body=mode switch {"unavailable"=>"tailscale_unavailable","sampler-failed"=>"sampler_unavailable","health-oversized"=>new string('x',1024),_=>"ok"};
                var response=$"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n{body}";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(response));
                if(mode=="flap"){await Task.Delay(100);Environment.Exit(45);}
            }
            catch(IOException){}
        }
    });
}
