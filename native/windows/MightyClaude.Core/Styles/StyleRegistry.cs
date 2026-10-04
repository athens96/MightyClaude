using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record RegisteredStyle(StyleManifest Manifest,string Source,string Path,string? WorkspacePath,string Hash,string Approval,ReadOnlyMemory<byte> Bytes)
{
    public string Id=>Manifest.Id;
    public bool Runnable=>Approval is "preApproved" or "approved";
    public StyleEvaluator Evaluator=>new(Manifest);
}
public sealed record StyleRejection(string Path,string Code,string Message);
public sealed record StyleApprovalRecord(string StyleId,string Source,string Path,string? WorkspacePath,string Hash,string State,DateTimeOffset DecidedAt);
public sealed class StyleTrustStore(string directory)
{
    private sealed record Store(int Version,List<StyleApprovalRecord> Records);
    public string DirectoryPath {get;}=System.IO.Path.GetFullPath(directory);
    private string FilePath=>System.IO.Path.Combine(DirectoryPath,"approvals.json");
    private static readonly object Gate=new();
    public static bool SamePath(string? a,string? b)=>string.Equals(a,b,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    private static bool SamePlace(StyleApprovalRecord r,RegisteredStyle s)=>r.Source==s.Source&&SamePath(r.Path,s.Path)&&SamePath(r.WorkspacePath,s.WorkspacePath);
    public List<StyleApprovalRecord> Load()
    {
        if(!File.Exists(FilePath))return [];
        if(!Private(DirectoryPath,true)||!Private(FilePath,false))throw new IOException(Locale.Get("settings.styles.lockBanner"));
        var bytes=StyleFiles.Read(DirectoryPath,"approvals.json",4*1024*1024)??throw new IOException(Locale.Get("settings.styles.lockBanner"));
        try{var store=JsonSerializer.Deserialize<Store>(bytes,Wire.Json);if(store is null||store.Version!=1||store.Records is null||store.Records.Count>256||store.Records.Any(r=>r is null||r.State is not("approved" or "revoked")||r.Source is not("user" or "workspace")||r.Hash is not {Length:64}))throw new JsonException();return store.Records;}
        catch(JsonException){throw new IOException(Locale.Get("settings.styles.lockBanner"));}
    }
    public string State(RegisteredStyle style)
    {
        if(style.Source=="bundled")return "preApproved";
        var records=Load().Where(r=>SamePlace(r,style));
        return records.Any(r=>r.State=="revoked")?"revoked":records.Any(r=>r.State=="approved"&&r.Hash==style.Hash)?"approved":"pending";
    }
    public void Decide(RegisteredStyle style,string state)
    {
        if(style.Source=="bundled")return;
        if(state is not("approved" or "revoked" or "unblock"))throw new ArgumentException(nameof(state));
        lock(Gate)
        {
            if(Directory.Exists(DirectoryPath)&&!Private(DirectoryPath,true))throw new IOException(Locale.Get("settings.styles.lockBanner"));
            Directory.CreateDirectory(DirectoryPath);Restrict(DirectoryPath,true);
            using var exclusive=new FileStream(System.IO.Path.Combine(DirectoryPath,"write.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var records=Load();
            if(state=="approved"&&records.Any(r=>SamePlace(r,style)&&r.State=="revoked"))throw new IOException(Locale.Get("settings.styles.unblockButton"));
            records.RemoveAll(r=>SamePlace(r,style)&&(state!="approved"||r.State=="approved"));
            if(state!="unblock")
            {
                while(records.Count>=256){var oldest=records.Where(r=>r.State=="approved").OrderBy(r=>r.DecidedAt).FirstOrDefault()??throw new IOException("Style approval store is full.");records.Remove(oldest);}
                records.Add(new(style.Id,style.Source,style.Path,style.WorkspacePath,style.Hash,state,DateTimeOffset.UtcNow));
            }
            var temp=System.IO.Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N")+".tmp");
            try{using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var bytes=JsonSerializer.SerializeToUtf8Bytes(new Store(1,records),Wire.Json);output.Write(bytes);output.Flush(true);}Restrict(temp,false);File.Move(temp,FilePath,true);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
    private static bool Private(string path,bool directory)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return false;
        if(OperatingSystem.IsWindows())
        {
            var security=directory?(FileSystemSecurity)new DirectoryInfo(path).GetAccessControl():new FileInfo(path).GetAccessControl();
            var current=WindowsIdentity.GetCurrent().User;
            if(current is null||!current.Equals(security.GetOwner(typeof(SecurityIdentifier))))return false;
            foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            {
                var sid=(SecurityIdentifier)rule.IdentityReference;
                if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&(FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership))!=0&&!sid.Equals(current)&&!sid.IsWellKnown(WellKnownSidType.LocalSystemSid)&&!sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))return false;
            }
            return true;
        }
        return (File.GetUnixFileMode(path)&(UnixFileMode.GroupWrite|UnixFileMode.OtherWrite))==0;
    }
    private static void Restrict(string path,bool directory)
    {
        if(OperatingSystem.IsWindows())
        {
            var user=WindowsIdentity.GetCurrent().User??throw new IOException("Windows account unavailable.");
            FileSystemSecurity security=directory?new DirectorySecurity():new FileSecurity();security.SetOwner(user);security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,directory?InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit:InheritanceFlags.None,PropagationFlags.None,AccessControlType.Allow));
            if(directory)new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);else new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
        else File.SetUnixFileMode(path,directory?UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute:UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
}
public sealed class StyleRegistry
{
    public IReadOnlyList<RegisteredStyle> Styles {get;}
    public IReadOnlyList<StyleRejection> Rejections {get;}
    public bool TrustLocked {get;}
    private StyleRegistry(List<RegisteredStyle> styles,List<StyleRejection> rejections,bool locked){Styles=styles;Rejections=rejections;TrustLocked=locked;}
    public RegisteredStyle? Runnable(string? id,string? hash)=>Styles.FirstOrDefault(s=>s.Id==id&&s.Runnable&&(s.Source=="bundled"||s.Hash==hash));
    public static StyleRegistry Load(string profile,string workspace)
    {
        var styles=new List<RegisteredStyle>();var errors=new List<StyleRejection>();var trust=new StyleTrustStore(System.IO.Path.Combine(profile,"style-trust"));
        var locked=false;
        try{trust.Load();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){locked=true;errors.Add(new(System.IO.Path.Combine(trust.DirectoryPath,"approvals.json"),"E_TRUST_LOCKED",e.Message));}
        void Add(byte[] bytes,string source,string path,string? root)
        {
            try
            {
                var manifest=StyleManifestDecoder.Decode(bytes,source);if(styles.Any(s=>s.Id==manifest.Id))throw new StyleManifestException("E_ID_COLLISION",manifest.Id);
                var style=new RegisteredStyle(manifest,source,path,root,Convert.ToHexStringLower(SHA256.HashData(bytes)),source=="bundled"?"preApproved":"pending",bytes);
                style=style with{Approval=locked?(source=="bundled"?"preApproved":"pending"):trust.State(style)};styles.Add(style);
            }
            catch(StyleManifestException e){errors.Add(new(path,e.Code,e.Message));}
            catch(IOException e){errors.Add(new(path,"E_TRUST_LOCKED",e.Message));}
        }
        var assembly=typeof(StyleRegistry).Assembly;
        foreach(var name in assembly.GetManifestResourceNames().Where(n=>n.StartsWith("MightyClaude.Core.Styles.",StringComparison.Ordinal)&&n.EndsWith(".json",StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {using var stream=assembly.GetManifestResourceStream(name)!;using var output=new MemoryStream();stream.CopyTo(output);Add(output.ToArray(),"bundled",name,null);}
        void Scan(string root,string relative,string source)
        {
            root=WorkspaceFiles.RealPath(root)??root;
            if(WorkspaceFiles.Resolve(relative,root) is not {} folder||!Directory.Exists(folder))return;
            try{foreach(var path in Directory.EnumerateFiles(folder).Take(1024).Order(StringComparer.Ordinal).Where(p=>p.EndsWith(".json",StringComparison.Ordinal)).Take(32))
            {var rel=System.IO.Path.GetRelativePath(root,path);if(StyleFiles.Read(root,rel,StyleManifestDecoder.MaximumBytes+1) is {} bytes)Add(bytes,source,WorkspaceFiles.RealPath(path)!,source=="workspace"?WorkspaceFiles.RealPath(workspace):null);}}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){errors.Add(new(folder,"E_READ",StyleText.Safe(e.Message)));}
        }
        Scan(profile,"styles","user");Scan(workspace,".claude/mighty-styles","workspace");return new(styles,errors,locked);
    }
    /// Re-read the exact location immediately before an approval or invocation.
    public static bool Unchanged(RegisteredStyle style)
    {
        if(style.Source=="bundled")return true;
        var root=style.WorkspacePath??System.IO.Path.GetDirectoryName(style.Path)!;
        return StyleFiles.Read(root,System.IO.Path.GetRelativePath(root,style.Path)) is {} bytes&&Convert.ToHexStringLower(SHA256.HashData(bytes))==style.Hash;
    }
}
