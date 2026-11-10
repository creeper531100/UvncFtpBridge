// mRemoteNG 整合（UvncFtpBridge 打包時加入，vnc.html 會 import 這支）：
//   • 上方常駐狀態列：沒連上時一直寫現在是什麼情況（跳板沒開、跳板 RDP 斷線、通道沒接上、機台連不到、密碼錯…），
//     取代 noVNC 原本一閃而過的「無法連線到伺服器」；
//   • 經跳板、通道沒接上時，按「連線」改成請 mRemoteNG 開跳板並部署跳板通道（執行 start.bat）；
//   • 通道恢復時自動重連一次（密碼錯誤不重連，避免機台封鎖 IP）。
// 狀態來源：自己每 2 秒問 UvncFtpBridge /api/route；在 mRemoteNG（WebView2）裡另外收 mRemoteNG 推來的跳板狀態與步驟。

const webview = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;

const M = {
    UI: null,
    route: null,       // 跳板路由；直連為 null
    machine: '',       // 機台 IP:埠
    jumpName: '',      // mRemoteNG 跳板連線名稱
    jump: null,        // mRemoteNG 回報的跳板 RDP 狀態：closed / login / reconnecting / ready
    online: null,      // 通道（File-Tunnel）是否接上
    step: '',          // mRemoteNG 目前的跳板通道步驟
    state: 'init',     // noVNC 的畫面狀態
    message: null,     // noVNC 自己要顯示的訊息（沒連上時改由狀態列顯示）
    authFailed: false,
    autoRetried: false,
    connectOnline: null, // 這次開始連線時通道是否已接上
    bar: null,

    start(UI) {
        M.UI = UI;
        M.parseTarget();
        M.createBar();
        M.hookUI();
        M.hookConnectButton();
        if (webview) {
            webview.addEventListener('message', e => M.onHostMessage(e.data));
            webview.postMessage('ready');
        }
        if (M.route) {
            M.poll();
            setInterval(M.poll, 2000);
        }
        M.render();
    },

    // path=ws/[路由~]機台[:埠]
    parseTarget() {
        let path = new URLSearchParams(location.search).get('path') || '';
        path = decodeURIComponent(path.replace(/^ws\//, ''));
        const at = path.indexOf('~');
        M.route = at > 0 ? path.substring(0, at) : null;
        M.machine = at > 0 ? path.substring(at + 1) : path;
    },

    createBar() {
        const bar = document.createElement('div');
        bar.id = 'mrng_bar';
        bar.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:100000;padding:10px 16px;' +
            'font:15px "Microsoft JhengHei UI","Microsoft JhengHei",sans-serif;text-align:center;line-height:1.5;' +
            'box-shadow:0 1px 4px rgba(0,0,0,.4);display:none;';
        document.body.appendChild(bar);
        M.bar = bar;
    },

    hookUI() {
        const UI = M.UI;
        const showStatus = UI.showStatus.bind(UI);
        const hideStatus = UI.hideStatus.bind(UI);
        // 連上後照 noVNC 原本的方式顯示；沒連上時收進狀態列，不再跳那條紅色橫幅
        UI.showStatus = (text, type, time) => {
            if (UI.connected) return showStatus(text, type, time);
            type = type || 'normal';
            // 「無法連線」之後 noVNC 還會再報一次「連線已中斷」：保留原本的錯誤原因
            if (M.message && M.message.type === 'error' && type === 'normal') return;
            M.message = { text: text, type: type };
            M.render();
        };
        UI.hideStatus = () => {
            M.message = null;
            hideStatus();
            M.render();
        };
        const updateVisualState = UI.updateVisualState.bind(UI);
        UI.updateVisualState = state => {
            M.state = state;
            if (state === 'connecting') {
                M.message = null;
                M.connectOnline = M.online;
            }
            if (state === 'connected') {
                M.authFailed = false;
                M.autoRetried = false;
                M.step = '';
            }
            updateVisualState(state);
            // 通道還沒接上時開始的連線失敗了，而現在通道已經好了：自動再連一次
            if (state === 'disconnected' && M.route && M.connectOnline !== true && M.online === true) M.tunnelBack();
            M.render();
        };
        const securityFailed = UI.securityFailed.bind(UI);
        UI.securityFailed = e => {
            M.authFailed = true;
            securityFailed(e);
        };
    },

    // 通道沒接上時，「連線」改成請 mRemoteNG 開跳板＋部署（start.bat）；捕獲階段先攔，noVNC 自己的 connect 不會跑
    hookConnectButton() {
        const button = document.getElementById('noVNC_connect_button');
        if (!button) return;
        button.addEventListener('click', e => {
            if (!M.needsJump()) return;
            e.stopImmediatePropagation();
            e.preventDefault();
            if (webview) {
                webview.postMessage('deploy');
                M.step = '正在開啟跳板並部署跳板通道，請查看跳板分頁...';
            } else {
                M.step = '請在 mRemoteNG 中開啟跳板，然後執行 [部署跳板通道]。';
            }
            M.render();
        }, true);
    },

    needsJump() {
        return M.route !== null && (M.jump === 'closed' || M.online === false);
    },

    async poll() {
        try {
            const response = await fetch('/api/route?id=' + encodeURIComponent(M.route),
                { headers: { 'X-UvncFtpBridge': '1' }, cache: 'no-store' });
            if (!response.ok) throw new Error(response.status);
            const json = await response.json();
            M.setOnline(json.online === true);
        } catch (err) {
            M.setOnline(false);
        }
    },

    setOnline(online) {
        const was = M.online;
        M.online = online;
        if (online && was === false) M.tunnelBack();
        M.render();
    },

    onHostMessage(data) {
        if (typeof data === 'string') {
            try { data = JSON.parse(data); } catch (err) { return; }
        }
        if (!data || typeof data !== 'object') return;
        if (data.type === 'jump' && (!M.route || data.route === M.route)) {
            const wasReady = M.jump === 'ready';
            M.jumpName = data.jumpName || M.jumpName;
            M.jump = data.jump;
            if (typeof data.online === 'boolean') {
                const was = M.online;
                M.online = data.online;
                if (data.online && was === false) M.tunnelBack();
            }
            if (!wasReady && M.jump === 'ready' && M.online) M.tunnelBack();
        } else if (data.type === 'step') {
            M.step = data.text || '';
        }
        M.render();
    },

    // 通道恢復：沒連上、不是密碼錯誤，就自動重連一次
    tunnelBack() {
        M.step = '';
        if (M.UI.connected || M.state === 'connecting' || M.authFailed || M.autoRetried) return;
        if (M.state !== 'disconnected' && M.state !== 'init') return;
        M.autoRetried = true;
        setTimeout(() => { if (!M.UI.connected) M.UI.connect(); }, 500);
    },

    jumpLabel() {
        return '「' + (M.jumpName || M.route) + '」';
    },

    // [文字, 等級] 等級：info／warn／error／ok。跳板／通道的問題優先：那時候「連線中」一定連不上。
    describe() {
        const jump = '跳板' + M.jumpLabel();
        if (M.route) {
            if (M.jump === 'closed') return [jump + '未開啟，無法連線到機台。按一下 [連線] 即可開啟跳板並自動部署跳板通道。', 'error'];
            if (M.jump === 'reconnecting') return [jump + '的遠端桌面正在重新連線，恢復後將自動接上。', 'warn'];
            if (M.jump === 'login') return [jump + '尚未登入，請到跳板分頁輸入密碼。', 'warn'];
            if (M.step) return [M.step, 'info'];
            if (M.online === false) return [jump + '的通道未連線 (跳板端未執行)。按一下 [連線] 即可在跳板部署跳板通道。', 'error'];
            if (M.online === null) return ['正在檢查跳板通道...', 'info'];
        }
        if (M.state === 'connecting') {
            return [(M.route ? '正在透過' + jump + '連線到機台 ' : '正在連線到機台 ') + M.machine + '...', 'info'];
        }
        if (M.authFailed) return ['機台 ' + M.machine + ' 拒絕連線，密碼可能不正確。請確認密碼，然後按一下 [連線]。', 'error'];
        if (M.message && M.message.type === 'error') {
            return [(M.route ? '跳板通道正常，但無法連線到機台 ' : '無法連線到機台 ') + M.machine + ': ' + M.message.text +
                    '。請確認機台已開機，且 IP 和連接埠正確，然後按一下 [連線] 再試一次。', 'error'];
        }
        if (M.message && M.message.type !== 'normal') return [M.message.text, 'warn'];
        if (M.state === 'disconnected' || M.state === 'init') {
            return ['已中斷連線' + (M.route ? ' (透過' + jump + ')' : '') + '。按一下 [連線] 即可重新連線。', 'warn'];
        }
        return ['', 'ok'];
    },

    render() {
        if (!M.bar) return;
        if (M.UI.connected) {
            M.bar.style.display = 'none';
            return;
        }
        const [text, level] = M.describe();
        if (!text) {
            M.bar.style.display = 'none';
            return;
        }
        const colors = {
            info: ['#E6F1FB', '#0C447C'],
            warn: ['#FAEEDA', '#633806'],
            error: ['#FCEBEB', '#791F1F'],
            ok: ['#EAF3DE', '#27500A'],
        }[level];
        M.bar.style.background = colors[0];
        M.bar.style.color = colors[1];
        M.bar.textContent = text;
        M.bar.style.display = 'block';
    },
};

export default M;
