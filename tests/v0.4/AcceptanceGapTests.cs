using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using LightView.App.Organizing;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class AcceptanceGapTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("conflict skip and ask preserve source target and undo stack", () => Conflicts(root, false)),
        ("late atomic conflict honors skip and ask without leaked temporary", () => Conflicts(root, true)),
        ("conflict settings persist and reject undefined policy", () => PolicySettings(root)),
        ("journal rotation retains malformed unresolved and recovery-copy logs", () => Archive(root)),
        ("case alias and Chinese spaces punctuation preserve bytes", () => Names(root)),
        ("read-only source copies and failed move never loses it", () => ReadOnly(root)),
        ("real isolated directory ACL denial preserves source", () => Denied(root)),
        ("occupied missing invalid and long target paths have safe outcomes", () => Paths(root)),
        ("disk-full write error cleans own temporary and never removes source", () => DiskFullError(root))
    ];
    private static void Check(bool ok, string why) { if (!ok) throw new IOException(why); }
    private static FileSnapshot Snapshot(string path) { using var stream=File.OpenRead(path); return FileSnapshot.Capture(path,stream); }
    private static (string Source,string Target,string Logs) Setup(string root,string name)
    {
        string dir=Path.Combine(root,name),target=Path.Combine(dir,"目标 [空格] & # 😀"); Directory.CreateDirectory(target);
        string source=Path.Combine(dir,"照片 测试 [01] & #.jpg"); File.WriteAllBytes(source,Enumerable.Range(0,8192).Select(i=>(byte)i).ToArray());
        return(source,target,Path.Combine(dir,"logs"));
    }
    private static void Conflicts(string root,bool late)
    {
        foreach(var policy in new[]{FileConflictPolicy.Skip,FileConflictPolicy.Ask})
        foreach(bool move in new[]{false,true})
        {
            var t=Setup(root,$"policy-{late}-{policy}-{move}");string dest=Path.Combine(t.Target,Path.GetFileName(t.Source));
            if(!late)File.WriteAllText(dest,"keep existing");
            var service=new SafeCopyService(new OperationJournal(t.Logs),(stage,record)=>
            {if(late&&stage=="CommitPlanned"&&!File.Exists(dest))File.WriteAllText(dest,"keep existing");});
            var request=new CopyRequest(Snapshot(t.Source),t.Target,"preset",ConflictPolicy:policy);
            var result=(move?service.MoveAsync(request):service.CopyAsync(request)).GetAwaiter().GetResult();
            Check(result.State==(policy==FileConflictPolicy.Skip?"Skipped":"Conflict"),result.Message);
            Check(File.Exists(t.Source)&&File.ReadAllText(dest)=="keep existing"&&Directory.GetFiles(t.Target).Length==1,"Conflict changed files");
            Check(service.UndoCount==0&&JournalRecovery.Read(t.Logs).Items.Count==0,"Clean refusal added undo/recovery work");
            var retry=(move?service.MoveAsync(request with{ConflictPolicy=FileConflictPolicy.Rename}):service.CopyAsync(request with{ConflictPolicy=FileConflictPolicy.Rename})).GetAwaiter().GetResult();
            Check(retry.State=="Completed"&&File.ReadAllText(dest)=="keep existing","Confirmed rename did not preserve both");
        }
    }
    private static void PolicySettings(string root)
    {
        var store=new LightView.App.Persistence.SettingsStore(Path.Combine(root,"policy-settings"));var settings=store.Load().Settings;
        Check(settings.ConflictPolicy==FileConflictPolicy.Rename,"Changed old-config default");settings.ConflictPolicy=FileConflictPolicy.Ask;store.Save(settings);
        Check(new LightView.App.Persistence.SettingsStore(Path.Combine(root,"policy-settings")).Load().Settings.ConflictPolicy==FileConflictPolicy.Ask,"Policy not saved");
        settings.ConflictPolicy=(FileConflictPolicy)99;Check(SettingsValidation.Error(settings)!=null,"Invalid policy accepted");
    }
    private static void Archive(string root)
    {
        var t=Setup(root,"journal-archive");Directory.CreateDirectory(t.Logs);var journal=new OperationJournal(t.Logs);var source=Snapshot(t.Source);
        var hashes=new Dictionary<string,byte[]>();
        foreach(var state in new[]{"Completed","Failed","Cancelled","Undone","Skipped","Conflict","Prepared","NeedsRecovery"})
        {
            var record=new CopyRecord(Guid.NewGuid().ToString("N"),state,source,t.Target,"",Kind:"Copy");journal.Append(record);
            string path=Path.Combine(t.Logs,record.OperationId+".jsonl");hashes.Add(path,File.ReadAllBytes(path));
        }
        var retained=new CopyRecord(Guid.NewGuid().ToString("N"),"Completed",source,t.Target,"",Kind:"UndoCopy");journal.Append(retained);
        string bad=Path.Combine(t.Logs,Guid.NewGuid().ToString("N")+".jsonl");File.WriteAllText(bad,"{broken");
        var result=JournalArchive.Rotate(t.Logs,DateTimeOffset.UtcNow,0);
        Check(result.Archived==6&&result.Warning==null,"Incorrect archive set");
        Check(File.Exists(bad)&&File.Exists(Path.Combine(t.Logs,retained.OperationId+".jsonl")),"Lost damaged/retained log");
        foreach(var archived in Directory.GetFiles(Path.Combine(t.Logs,"Archive"),"*.jsonl",SearchOption.AllDirectories))
            Check(File.ReadAllBytes(archived).SequenceEqual(hashes[Path.Combine(t.Logs,Path.GetFileName(archived))]),"Archive changed original bytes");
        var scan=JournalRecovery.Read(t.Logs,true);Check(scan.Items.Count==4,"Unresolved/recovery copy hidden");
        Check(JournalArchive.Rotate(t.Logs,DateTimeOffset.UtcNow,0).Archived==0,"Rotation not idempotent");
    }
    private static void Names(string root)
    {
        var t=Setup(root,"names-case");var service=new SafeCopyService(new OperationJournal(t.Logs));var snap=Snapshot(t.Source);
        var same=service.CopyAsync(new(snap,Path.GetDirectoryName(t.Source)!.ToUpperInvariant(),"case",Path.GetFileName(t.Source).ToUpperInvariant())).GetAwaiter().GetResult();
        Check(same.State=="NoOp","Windows case alias not recognized");
        var copy=service.CopyAsync(new(snap,t.Target,"special")).GetAwaiter().GetResult();Check(copy.State=="Completed",copy.Message);
        Check(File.ReadAllBytes(t.Source).SequenceEqual(File.ReadAllBytes(copy.Destination!)),"Unicode path altered bytes");
    }
    private static void ReadOnly(string root)
    {
        var t=Setup(root,"read-only");var snap=Snapshot(t.Source);File.SetAttributes(t.Source,FileAttributes.ReadOnly);
        try
        {
            var service=new SafeCopyService(new OperationJournal(t.Logs));var copy=service.CopyAsync(new(snap,t.Target,"readonly")).GetAwaiter().GetResult();Check(copy.State=="Completed",copy.Message);
            var move=service.MoveAsync(new(snap,t.Target,"readonly")).GetAwaiter().GetResult();
            Check(move.State is "Completed" or "Failed",move.Message);
            Check(move.State=="Completed"?File.Exists(move.Destination):File.Exists(t.Source),"Read-only move lost file");
        }
        finally { if(File.Exists(t.Source))File.SetAttributes(t.Source,FileAttributes.Normal);foreach(string path in Directory.GetFiles(t.Target))File.SetAttributes(path,FileAttributes.Normal); }
    }
    private static void Denied(string root)
    {
        var t=Setup(root,"acl-denied");var directory=new DirectoryInfo(t.Target);var original=directory.GetAccessControl();
        var deny=directory.GetAccessControl();var sid=WindowsIdentity.GetCurrent().User!;
        deny.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.CreateFiles,AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(deny);
            var result=new SafeCopyService(new OperationJournal(t.Logs)).CopyAsync(new(Snapshot(t.Source),t.Target,"denied")).GetAwaiter().GetResult();
            Check(result.State=="Failed"&&File.Exists(t.Source)&&Directory.GetFiles(t.Target).Length==0,"ACL denial unsafe or not applied: "+result.Message);
        }
        finally { directory.SetAccessControl(original); }
    }
    private static void Paths(string root)
    {
        foreach(string mode in new[]{"missing","occupied","invalid","long"})
        {
            var t=Setup(root,"path-"+mode);string target=t.Target;
            if(mode=="missing")target=Path.Combine(target,"absent");
            if(mode=="occupied"){File.WriteAllText(Path.Combine(target,Path.GetFileName(t.Source)),"existing");}
            if(mode=="invalid")target=target+Path.DirectorySeparatorChar+"bad\0path";
            if(mode=="long"){while(target.Length<300)target=Path.Combine(target,"long-folder-1234567890");Directory.CreateDirectory(target);}
            var service=new SafeCopyService(new OperationJournal(t.Logs));var result=service.CopyAsync(new(Snapshot(t.Source),target,"paths")).GetAwaiter().GetResult();
            Check(File.Exists(t.Source),"Path error removed source");
            Check(mode is "missing" or "invalid"?result.State=="Failed":result.State=="Completed",mode+": "+result.Message);
        }
    }
    private static void DiskFullError(string root)
    {
        var t=Setup(root,"disk-full-error");
        foreach(bool move in new[]{false,true})
        {
            string target=move?Path.Combine(Path.GetTempPath(),"LightView-disk-full-injection-"+Guid.NewGuid().ToString("N")):t.Target;
            Directory.CreateDirectory(target);
            if(move) Check(Path.GetPathRoot(target)!=Path.GetPathRoot(t.Source),"This cross-volume acceptance needs a second local volume");
            var service=new SafeCopyService(new OperationJournal(t.Logs),(stage,_)=>{if(stage=="Transferring")throw new IOException("Injected ERROR_DISK_FULL",unchecked((int)0x80070070));});
            var request=new CopyRequest(Snapshot(t.Source),target,"full");
            var result=(move?service.MoveAsync(request):service.CopyAsync(request)).GetAwaiter().GetResult();
            Check(result.State=="Failed"&&File.Exists(t.Source)&&Directory.GetFiles(target).Length==0,"Disk-full error cleanup unsafe");
            if(move) File.WriteAllText(Path.Combine(Path.GetDirectoryName(t.Source)!,"cross-volume-injection-target.txt"),target);
        }
    }
}
