using Tailwatch.Core;
namespace Tailwatch.App;
internal sealed class SettingsForm : Form
{
    public SettingsForm(TailwatchMonitor monitor,Action save)
    {
        var networkKey=monitor.NetworkKey;
        Text="Tailwatch · Machines";Size=new Size(620,430);StartPosition=FormStartPosition.CenterScreen;
        BackColor=Theme.Background;ForeColor=Theme.Text;Font=new Font("Segoe UI",9f);MinimizeBox=false;MaximizeBox=false;
        var note=new Label{Text="Choose machines in the active tailnet. Each needs a running Tailwatch agent.",Dock=DockStyle.Top,Height=48,Padding=new Padding(12)};
        var grid=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Theme.Background,
            BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false,GridColor=Theme.Border};
        grid.DefaultCellStyle.BackColor=Theme.Card;grid.DefaultCellStyle.ForeColor=Theme.Text;
        grid.ColumnHeadersDefaultCellStyle.BackColor=Theme.Background;grid.ColumnHeadersDefaultCellStyle.ForeColor=Theme.Text;
        grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="Monitor",FillWeight=20});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="Machine",ReadOnly=true,FillWeight=65});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="Status",ReadOnly=true,FillWeight=45});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="Port",FillWeight=22});
        foreach(var row in monitor.Machines){var n=grid.Rows.Add(row.Monitored,row.Peer.Name,row.Status,monitor.GetPort(row.Peer.Id));grid.Rows[n].Tag=row.Peer.Id;}
        var apply=Theme.Button("Save",(_,_)=>{
            if(monitor.NetworkKey!=networkKey){MessageBox.Show("The active tailnet changed. Reopen Machines to edit the current network.");Close();return;}
            grid.EndEdit();
            foreach(DataGridViewRow row in grid.Rows)
                if(!int.TryParse(row.Cells["Port"].Value?.ToString(),out int port)||port is <1 or >65535)
                {MessageBox.Show("Ports must be between 1 and 65535.");return;}
            foreach(DataGridViewRow row in grid.Rows)
            {
                var id=(string)row.Tag!;monitor.SetMonitored(id,row.Cells["Monitor"].Value is true);
                monitor.SetPort(id,int.Parse(row.Cells["Port"].Value!.ToString()!));
            }
            save();DialogResult=DialogResult.OK;Close();
        });apply.Dock=DockStyle.Bottom;apply.Height=38;
        Controls.Add(grid);Controls.Add(note);Controls.Add(apply);
        var self=monitor.Machines.FirstOrDefault(m=>m.Peer.IsSelf);
        if(self!=null){var address=new TextBox{Dock=DockStyle.Bottom,ReadOnly=true,Text="Viewer Tailscale IP: "+string.Join(", ",self.Peer.Addresses),BackColor=Theme.Card,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle};Controls.Add(address);address.BringToFront();}
    }
}
