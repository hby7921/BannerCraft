using System.Text;
using Mycraft.UI;

int checks=0;
void Check(bool test,string message){checks++;if(!test)throw new Exception(message);}
const string token="0123456789abcdef0123456789abcdef";
byte[] Frame(int width=2,int height=1,long time=10000,long ack=0,long number=1,int flags=1,string id=token,int extra=8)
{
    using var bytes=new MemoryStream();using var writer=new BinaryWriter(bytes,Encoding.ASCII,true);
    writer.Write(VanillaInventoryTransport.Magic);writer.Write(width);writer.Write(height);writer.Write(time);writer.Write(ack);writer.Write(number);writer.Write(Encoding.ASCII.GetBytes(id));writer.Write(flags);writer.Write(new byte[extra]);return bytes.ToArray();
}
VanillaInventoryTransport.Frame? Read(byte[] bytes)=>VanillaInventoryTransport.ReadFrame(new MemoryStream(bytes),token,10000);
Check(VanillaInventoryTransport.ValidId(token),"valid scoped session");
Check(!VanillaInventoryTransport.ValidId("../other"),"path traversal rejected");
Check(!VanillaInventoryTransport.ValidId(token.ToUpperInvariant()),"session alphabet constrained");
var valid=Read(Frame());
Check(valid?.Open==true && valid.Width==2 && valid.Height==1 && valid.Pixels.Length==8,"little-endian Java header and RGBA body");
Check(Read(Frame(time:6000))==null,"stale image cannot authorize input");
Check(Read(Frame(time:12000))==null,"future image rejected");
Check(Read(Frame(time:long.MinValue))==null,"timestamp overflow rejected");
Check(Read(Frame(ack:-1))==null,"negative ack rejected");
Check(Read(Frame(number:0))==null,"zero frame sequence rejected");
Check(Read(Frame(id:new string('f',32)))==null,"old open token rejected");
Check(Read(Frame(width:0))==null,"zero-size open frame rejected");
Check(Read(Frame(width:int.MaxValue))==null,"width overflow rejected before allocation");
Check(Read(Frame(height:721))==null,"720p height cap");
Check(Read(Frame(extra:7))==null,"truncated RGBA rejected");
Check(Read(Frame(extra:9))==null,"trailing bytes rejected");
Check(Read(Frame(flags:2))==null,"unknown state rejected");
Check(Read(Frame(width:0,height:0,flags:0,extra:0))?.Open==false,"explicit remote screen close");
Check(Read(Frame(flags:0))==null,"close frame cannot smuggle pixels");
Check(Read(new byte[71])==null,"short header rejected");
Check(VanillaInventoryTransport.ReadFrame(new MemoryStream(Frame()),token,10000,1)==null,"duplicate frame skipped before pixel allocation");
Console.WriteLine($"PASS: vanilla inventory {checks} frame/session assertions");
