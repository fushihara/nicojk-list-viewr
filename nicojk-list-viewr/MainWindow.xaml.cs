using IniParser;
using IniParser.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace WpfApp1 {
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window {
        private MainWindowModel model = new MainWindowModel();
        private MainWindowView view = new MainWindowView();
        public MainWindow() {
            InitializeComponent();
            //this.stationSelection.DataContext = this.局選択のobservable;
            this.DataContext = this.view;
            this.view.statusProgressbar = this.statusProgressbar;
            this.view.statusMessage = this.statusMessage;
            this.stationSelection.SelectionChanged += (e, sender) => {
                var e2 = e as System.Windows.Controls.ComboBox;
                var s2 = sender as System.Windows.Controls.SelectionChangedEventArgs;
                if (s2.AddedItems.Count == 0) {
                    return;
                }
                var addItem = s2.AddedItems[0] as MainWindowView.StationList;
                this.view.refreshStationSelection(addItem.jkNo);
            };
        }
        private void Window_Loaded(object sender, RoutedEventArgs e) {
            this.model.onDataReload += (jkDatas, loadDatas) => {
                this.view.setChannelList(jkDatas);
                this.view.setJkListData(loadDatas);
            };
            this.model.onOneDataUpdate += (処理相性の合計個数, 処理済みの個数, jkId, fileDate, startDate, endDate) => {
                this.Dispatcher.Invoke(new Action<int, int, int, long, DateTime, DateTime>(this.view.updateOneData), 処理相性の合計個数, 処理済みの個数, jkId, fileDate, startDate, endDate);
            };
            this.model.init(this.Dispatcher);
        }
    }
    class MainWindowModel {
        public delegate void DataReloadHander(List<IniJkNames> jkDatas, List<JkFileData> loadData);
        public event DataReloadHander onDataReload;
        public delegate void OneDataUpdateHander(int 処理対象の分母, int 処理対象の分子, int jkId, long fileDate, DateTime startDate, DateTime endDate);
        public event OneDataUpdateHander onOneDataUpdate;
        private SQLiteConnection sqliteConnection;
        private string ディレクトリのパス { get; set; } = "";
        private List<IniJkNames> jkの名前定義一覧 { get; set; } = new List<IniJkNames>();
        private List<JkFileData> すべてのファイルの一覧 { get; set; } = new List<JkFileData>();
        private Task getFileDetailsTask;
        public class IniJkNames {
            public string 局名 { get; set; } = "";
            public int jk番号 { get; set; } = 0;
            public long ファイル数 { get; set; } = 0;
            public long ファイル合計容量 { get; set; } = 0;
        }
        public class JkFileData {
            public int jk番号 { get; set; } = 0;
            public long ファイル番号 { get; set; } = 0;
            public long ファイルサイズ { get; set; } = 0;
            public DateTime? 最初のコメントの日時 { get; set; } = null;
            public DateTime? 最後のコメントの日時 { get; set; } = null;
            public string ファイルのフルパス { get; set; } = "";
        }
        public void init(Dispatcher dispatcher) {
            this.setupIniFile();
            this.setupSqlite();
            this.getJkFiles();
            this.onDataReload(this.jkの名前定義一覧, this.すべてのファイルの一覧);
            this.getFileDetailsTask = new Task(getFileDetails);
            this.getFileDetailsTask.Start();
        }
        private void setupIniFile() {
            const string sqliteのファイル名 = "jkData.ini";
            if (System.IO.File.Exists(sqliteのファイル名) == false) {
                System.IO.File.WriteAllText(sqliteのファイル名, $@"
rootDirectory=./nicoJk/
jknames.1=NHK総合
jknames.2=NHK教育
jknames.4=日テレ
jknames.5=テレ朝
jknames.6=TBS
jknames.7=テレビ東京
jknames.8=フジテレビ
jknames.9=TOKYO MX
");
            }
            var parser = new FileIniDataParser();
            IniData data = parser.ReadFile(sqliteのファイル名, Encoding.UTF8);
            var jkNamesRegex = new Regex(@"jknames.(\d+)", RegexOptions.IgnoreCase);
            foreach (KeyData section in data.Global) {
                Console.WriteLine(section.KeyName + " = " + section.Value);
                var jkNamesMatchR = jkNamesRegex.Match(section.KeyName);
                if (section.KeyName == "rootDirectory") {
                    this.ディレクトリのパス = section.Value;
                } else if (jkNamesMatchR.Success) {
                    var jkId = int.Parse(jkNamesMatchR.Groups[1].ToString());
                    var stationName = section.Value;
                    var resultDataJkIdExist = false;
                    foreach (var a in this.jkの名前定義一覧) {
                        if (a.jk番号 == jkId) {
                            resultDataJkIdExist = true;
                            break;
                        }
                    }
                    if (resultDataJkIdExist == false) {
                        this.jkの名前定義一覧.Add(new IniJkNames {
                            jk番号 = jkId,
                            局名 = stationName
                        });
                    }
                }
            }
        }
        private void setupSqlite() {
            const string sqliteのファイル名 = "jkDatabase.db";
            string sqliteの接続文字列 = (new SQLiteConnectionStringBuilder { DataSource = sqliteのファイル名 }).ToString();
            if (System.IO.File.Exists(sqliteのファイル名) == false) {
                using (var cn = new SQLiteConnection(sqliteの接続文字列)) {
                    cn.Open();
                    using (var cmd = new SQLiteCommand(cn)) {
                        cmd.CommandText = $@"
create table jkFile(
  jkNo integer not null,
  fileNumber integer not null,
  fileSize integer not null,
  vopsStartDate text,
  vopsEndDate   text,
  PRIMARY KEY(`jkNo`,`fileNumber`)
);";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            // ファイルの準備ok
            {
                using (var cn = new SQLiteConnection(sqliteの接続文字列)) {
                    cn.Open();
                    using (var cmd = new SQLiteCommand(cn)) {
                        cmd.CommandText = $@"PRAGMA user_version";
                        SQLiteDataReader sdr = cmd.ExecuteReader();
                        if (sdr.Read()) {
                            long userVersion = (long)sdr["user_version"];//このキャストはdoubleでもintでもダメ。longにする必要あり。
                            Console.WriteLine($"dbのuserVersionは{userVersion}です");
                        }
                    }
                }
            }
            // バージョンも含めてOK。
            {
                //var cn = new SQLiteConnection(sqliteの接続文字列);
                //cn.Open();
                // cn.Dispose();
                //this.sqliteConnection = cn;
            }
        }
        private void getJkFiles() {
            Console.WriteLine($"getJkFiles開始");
            var subDirectoryPaths = Directory.GetDirectories(this.ディレクトリのパス, "*", SearchOption.TopDirectoryOnly);
            var jkNamesRegex = new Regex(@"^jk(\d+)$", RegexOptions.IgnoreCase);
            var jkFileNamesRegex = new Regex(@"^(\d+)\.txt$", RegexOptions.IgnoreCase);
            var 局名定義一覧 = this.jkの名前定義一覧.ToDictionary(data => { return data.jk番号; });
            foreach (var fullPath in subDirectoryPaths) {
                var dirName = Path.GetFileName(fullPath);
                var m = jkNamesRegex.Match(dirName);
                if (!m.Success) {
                    continue;
                }
                var jkIdInPath = int.Parse(m.Groups[1].ToString());
                var allFiles = Directory.GetFiles(fullPath, "*.txt", SearchOption.TopDirectoryOnly);
                // jk10 のdbの情報を全部持ってくる
                var sqliteDatas = new List<dynamic>();
                Console.WriteLine($" jk{jkIdInPath} を取得開始。ファイル数は {allFiles.Length}個");
                const string sqliteのファイル名 = "jkDatabase.db";
                string sqliteの接続文字列 = (new SQLiteConnectionStringBuilder { DataSource = sqliteのファイル名 }).ToString();
                using (var cn = new SQLiteConnection(sqliteの接続文字列)) {
                    cn.Open();
                    using (var cmd = new SQLiteCommand(cn)) {
                        cmd.CommandText = @"select * from jkFile where jkNo = @jkNo and vopsStartDate is not null and vopsEndDate is not null";
                        cmd.Parameters.Add(new SQLiteParameter("@jkNo", jkIdInPath));
                        cmd.Prepare();
                        using (var reader = cmd.ExecuteReader()) {
                            while (reader.Read()) {
                                var fileNumber = (long)reader["fileNumber"];
                                var fileSize = (long)reader["fileSize"];
                                var vopsStartDate = (string)reader["vopsStartDate"];
                                var vopsEndDate = (string)reader["vopsEndDate"];
                                sqliteDatas.Add(new {
                                    fileNumber,
                                    fileSize,
                                    vopsStartDate,
                                    vopsEndDate
                                });
                            }
                        }
                    }
                }
                Console.WriteLine($"  DBから {sqliteDatas.Count} 件のデータを取得");
                var sqliteから受信したデータ = sqliteDatas.Select(a => {
                    return new {
                        fileNumber = (long)a.fileNumber,
                        fileSize = (long)a.fileSize,
                        vopsStartDate = DateTime.Parse((string)a.vopsStartDate),
                        vopsEndDate = DateTime.Parse((string)a.vopsEndDate),
                    };
                });
                var sqliteから受信したデータd = sqliteから受信したデータ.ToDictionary(data => {
                    return data.fileNumber + "-" + data.fileSize;
                });
                foreach (var file in allFiles) {
                    var mc = jkFileNamesRegex.Match(Path.GetFileName(file));
                    if (!mc.Success) {
                        continue;
                    };
                    var fileTimestamp = int.Parse(mc.Groups[1].ToString());
                    var fileSize = new FileInfo(file).Length;
                    var sqliteから受信したデータf = sqliteから受信したデータd.ContainsKey(fileTimestamp + "-" + fileSize) ? sqliteから受信したデータd[fileTimestamp + "-" + fileSize] : null;
                    /*
                     * ↓なぜかめちゃくちゃ遅い
                    var sqliteから受信したデータf = sqliteから受信したデータ.Where(data => {
                        return data.fileSize == fileSize && data.fileNumber == fileTimestamp;
                    }).FirstOrDefault();
                    */
                    if (局名定義一覧.ContainsKey(jkIdInPath)) {
                        局名定義一覧[jkIdInPath].ファイル数 += 1;
                        局名定義一覧[jkIdInPath].ファイル合計容量 += fileSize;
                    }
                    if (sqliteから受信したデータf != null) {
                        this.すべてのファイルの一覧.Add(new JkFileData {
                            jk番号 = jkIdInPath,
                            ファイル番号 = fileTimestamp,
                            ファイルサイズ = fileSize,
                            最初のコメントの日時 = sqliteから受信したデータf.vopsStartDate,
                            最後のコメントの日時 = sqliteから受信したデータf.vopsEndDate,
                            ファイルのフルパス = file
                        });
                    } else {
                        this.すべてのファイルの一覧.Add(new JkFileData {
                            jk番号 = jkIdInPath,
                            ファイル番号 = fileTimestamp,
                            ファイルサイズ = fileSize,
                            ファイルのフルパス = file
                        });
                    }
                }
            }
            Console.WriteLine($"getJkFiles終了");
        }
        private void getFileDetails() {
            // 処理前に トータル個数をチェック
            var 処理相性の合計個数 = this.すべてのファイルの一覧.Where(data => {
                return data.最初のコメントの日時 == null || data.最後のコメントの日時 == null;
            }).Count();
            var 処理済みの個数 = 0;
            foreach (var data in this.すべてのファイルの一覧) {
                if (data.最初のコメントの日時 != null) {
                    continue;
                }
                DateTime? startDate = null;
                DateTime? endDate = null;
                using (System.IO.StreamReader sr = new System.IO.StreamReader(data.ファイルのフルパス, Encoding.UTF8)) {
                    var dateRegEx = new Regex(@"date=""(\d{10})""");
                    while (sr.Peek() > -1) {
                        var line = sr.ReadLine().Trim();
                        var match = dateRegEx.Match(line);
                        if (match.Success == false) {
                            continue;
                        }
                        var unixTimeSec = long.Parse(match.Groups[1].ToString());
                        var localDate = DateTimeOffset.FromUnixTimeSeconds(unixTimeSec).LocalDateTime;
                        if (startDate == null || localDate < startDate) {
                            startDate = localDate;
                        }
                        if (endDate == null || endDate < localDate) {
                            endDate = localDate;
                        }
                    }
                }
                if (startDate == null) {
                    continue;
                }
                if (endDate == null) {
                    continue;
                }
                try {
                    const string sqliteのファイル名 = "jkDatabase.db";
                    string sqliteの接続文字列 = (new SQLiteConnectionStringBuilder { DataSource = sqliteのファイル名 }).ToString();
                    using (var cn = new SQLiteConnection(sqliteの接続文字列)) {
                        cn.Open();
                        using (var cmd = new SQLiteCommand(cn)) {
                            cmd.CommandText = @" replace into jkFile(jkNo,fileNumber,fileSize,vopsStartDate,vopsEndDate) values(@jkNo,@fileNumber,@fileSize,@vopsStartDate,@vopsEndDate)";
                            cmd.Parameters.Add(new SQLiteParameter("@jkNo", data.jk番号));
                            cmd.Parameters.Add(new SQLiteParameter("@fileNumber", data.ファイル番号));
                            cmd.Parameters.Add(new SQLiteParameter("@fileSize", data.ファイルサイズ));
                            cmd.Parameters.Add(new SQLiteParameter("@vopsStartDate", startDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
                            cmd.Parameters.Add(new SQLiteParameter("@vopsEndDate", endDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
                            cmd.Prepare();
                            cmd.ExecuteNonQuery();
                        }
                    }
                    処理済みの個数 += 1;
                    this.onOneDataUpdate(処理相性の合計個数, 処理済みの個数, data.jk番号, data.ファイル番号, startDate.Value, endDate.Value);
                } catch (Exception e) {
                    Console.WriteLine(e);
                }
            }
        }
    }
    class MainWindowView {
        public System.Windows.Controls.TextBlock statusMessage;
        public System.Windows.Controls.ProgressBar statusProgressbar;
        private Dictionary<int, MainWindowModel.IniJkNames> jkNameList = new Dictionary<int, MainWindowModel.IniJkNames>();
        // このobservableCollectionにはgetterとsetter必須。無いと動かない
        public ObservableCollection<JkItem> gridViewObservableCollection { get; set; } = new ObservableCollection<JkItem>();
        public CollectionViewSource gridViewViewSource { get; set; } = new CollectionViewSource();
        public ObservableCollection<StationList> stationListObservableCollection { get; set; } = new ObservableCollection<StationList>();
        public int filterStationJkNo = 0;
        public class JkItem : INotifyPropertyChanged {
            public event PropertyChangedEventHandler PropertyChanged;
            private void NotifyPropertyChanged([CallerMemberName] String propertyName = "") {
                if (PropertyChanged != null) {
                    PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
                }
            }
            private long _ファイル番号 = 0;
            private int _jk番号 = 0;
            private string _局名 = "";
            private long _ファイルサイズ = 0;
            private DateTime? _開始時刻 = null;
            private DateTime? _終了時刻 = null;
            public DateTime ファイルの日時 {
                get {
                    return DateTimeOffset.FromUnixTimeSeconds(ファイル番号).LocalDateTime;
                }
            }
            public double? 開始から終了までの分 {
                get {
                    var 開始 = this.開始時刻;
                    var 終了 = this.終了時刻;
                    if (開始 == null) {
                        return null;
                    }
                    if (終了 == null) {
                        return null;
                    }
                    return (終了.Value - 開始.Value).TotalMinutes;
                }
            }
            public long ファイル番号 {
                get { return this._ファイル番号; }
                set {
                    if (value == this._ファイル番号) {
                        return;
                    }
                    this._ファイル番号 = value;
                    this.NotifyPropertyChanged();
                }
            }
            public int jk番号 {
                get { return this._jk番号; }
                set {
                    if (value == this._jk番号) {
                        return;
                    }
                    this._jk番号 = value;
                    this.NotifyPropertyChanged();
                }
            }
            public string 局名 {
                get { return this._局名; }
                set {
                    if (value == this._局名) {
                        return;
                    }
                    this._局名 = value;
                    this.NotifyPropertyChanged();
                }
            }
            public double ファイルサイズkb {
                get {
                    return this.ファイルサイズ / 1024.0;
                }
            }
            public long ファイルサイズ {
                get { return this._ファイルサイズ; }
                set {
                    if (value == this._ファイルサイズ) {
                        return;
                    }
                    this._ファイルサイズ = value;
                    this.NotifyPropertyChanged();
                    this.NotifyPropertyChanged("ファイルサイズkb");
                }
            }
            public DateTime? 開始時刻 {
                get { return this._開始時刻; }
                set {
                    if (value == this._開始時刻) {
                        return;
                    }
                    this._開始時刻 = value;
                    this.NotifyPropertyChanged();
                    this.NotifyPropertyChanged("開始から終了までの分");
                }
            }
            public DateTime? 終了時刻 {
                get { return this._終了時刻; }
                set {
                    if (value == this._終了時刻) {
                        return;
                    }
                    this._終了時刻 = value;
                    this.NotifyPropertyChanged();
                    this.NotifyPropertyChanged("開始から終了までの分");
                }
            }
        }
        public class StationList : INotifyPropertyChanged {
            public event PropertyChangedEventHandler PropertyChanged;
            private void NotifyPropertyChanged([CallerMemberName] String propertyName = "") {
                if (PropertyChanged != null) {
                    PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
                }
            }
            public string 局を選択するコンボボックスで表示するテキスト {
                get {
                    // サイズ部分を作る
                    // 12354 files. 12345.00 kbyte
                    var fileSizeStr = "";
                    if (this._ファイル個数 != 0) {
                        var sizeMb = this._ファイル合計容量 / 1024.0 /1024.0;
                        fileSizeStr = $" {this._ファイル個数,4} files. {sizeMb,8:0.0} mbyte";
                    }
                    var stationName = "";
                    if (this._局名 != "") {
                        stationName = $"{this._局名}(jk{this._jkNo})";
                    } else {
                        stationName = $"jk{this._jkNo}";
                    }
                    return $"{stationName.PadRight(22 - (Encoding.GetEncoding("Shift_JIS").GetByteCount(stationName) - stationName.Length))} {fileSizeStr}";
                }
            }
            private int _jkNo = 0;
            public int jkNo {
                get {
                    return this._jkNo;
                }
                set {
                    if (value == this._jkNo) {
                        return;
                    }
                    this._jkNo = value;
                    this.NotifyPropertyChanged();
                    this.NotifyPropertyChanged(局を選択するコンボボックスで表示するテキスト);
                }
            }
            private string _局名 = "";
            public string 局名 {
                get {
                    return this._局名;
                }
                set {
                    if (value == this._局名) {
                        return;
                    }
                    this._局名 = value;
                    this.NotifyPropertyChanged();
                    this.NotifyPropertyChanged(局を選択するコンボボックスで表示するテキスト);
                }
            }
            private long _ファイル個数 = 0;
            public long ファイル個数 {
                get {
                    return this._ファイル個数;
                }
                set {
                    if (value == this._ファイル個数) {
                        return;
                    }
                    this._ファイル個数 = value;
                    this.NotifyPropertyChanged();
                    this.NotifyPropertyChanged(局を選択するコンボボックスで表示するテキスト);
                }
            }
            private long _ファイル合計容量 = 0;
            public long ファイル合計容量 {
                get {
                    return this._ファイル合計容量;
                }
                set {
                    if (value == this._ファイル合計容量) {
                        return;
                    }
                    this._ファイル合計容量 = value;
                    this.NotifyPropertyChanged();
                    this.NotifyPropertyChanged(局を選択するコンボボックスで表示するテキスト);
                }
            }
        }
        public MainWindowView() {
            var view = CollectionViewSource.GetDefaultView(this.gridViewObservableCollection);
            view.SortDescriptions.Add(new SortDescription("ファイル番号", ListSortDirection.Descending));
            this.stationListObservableCollection.Add(new StationList {
                jkNo = 0,
                局名 = "全て"
            });
            this.gridViewViewSource.Source = this.gridViewObservableCollection;
            this.gridViewViewSource.Filter += GridViewViewSource_Filter;
        }
        public void refreshStationSelection(int stationJkNo) {
            this.filterStationJkNo = stationJkNo;
            this.gridViewViewSource.View.Refresh();
        }

        private void GridViewViewSource_Filter(object sender, FilterEventArgs e) {
            //throw new NotImplementedException();
            if (e.Item == null) {
                e.Accepted = true;
                return;
            }
            if (this.filterStationJkNo == 0) {
                e.Accepted = true;
                return;
            }
            var e2 = e.Item as JkItem;
            if (e2 == null) {
                e.Accepted = true;
                return;
            }
            e.Accepted = e2.jk番号 == this.filterStationJkNo;
            return;
        }

        public void setChannelList(IEnumerable<MainWindowModel.IniJkNames> a) {
            // 上のドロップダウンをセットする
            this.jkNameList = a.ToDictionary(data => { return data.jk番号; });
            foreach (var data in a) {
                this.stationListObservableCollection.Add(new StationList {
                    jkNo = data.jk番号,
                    局名 = data.局名,
                    ファイル個数 = data.ファイル数,
                    ファイル合計容量 = data.ファイル合計容量
                });
            }
        }
        public int getChannelJkId() {
            return 0;
        }
        public void setJkListData(IEnumerable<MainWindowModel.JkFileData> datas) {
            // 下のgridViewを全部更新する
            this.gridViewObservableCollection.Clear();
            foreach (var data in datas) {
                var 局名 = $"jk{data.jk番号}";
                if (this.jkNameList.ContainsKey(data.jk番号)) {
                    局名 = this.jkNameList[data.jk番号].局名;
                }
                this.gridViewObservableCollection.Add(new JkItem {
                    ファイル番号 = data.ファイル番号,
                    局名 = 局名,
                    jk番号 = data.jk番号,
                    ファイルサイズ = data.ファイルサイズ,
                    開始時刻 = data.最初のコメントの日時,
                    終了時刻 = data.最後のコメントの日時
                });
            }
        }
        public void updateOneData(int 処理対象の合計個数, int 処理済みの個数, int jkId, long fileDate, DateTime startDate, DateTime endDate) {
            // 下のgridViewから一つ更新する
            this.statusMessage.Text = $"ファイル {処理済みの個数}/{処理対象の合計個数} を読み込み中";
            this.statusProgressbar.Maximum = 処理対象の合計個数;
            this.statusProgressbar.Value = 処理済みの個数;
            this.statusProgressbar.IsIndeterminate = false;
            foreach (var data in this.gridViewObservableCollection) {
                if (data.jk番号 == jkId && data.ファイル番号 == fileDate) {
                    data.開始時刻 = startDate;
                    data.終了時刻 = endDate;
                }
            }
        }

    }
}
