// MaxHub Web Portal 导航渲染与角色感知
// 页面在 body 顶部放 <nav class="nav" id="topnav"></nav>，加载本脚本自动填充。
(function () {
  function renderNav(user) {
    const nav = document.getElementById('topnav');
    if (!nav) return;
    const page = location.pathname.split('/').pop() || 'index.html';
    const roles = user?.roles ?? [];

    // 品牌 logo：与 Agent 应用图标同源的等距立方体（三面蓝色明度阶梯）
    const logo = `<svg class="logo" width="18" height="18" viewBox="0 0 32 32" aria-hidden="true">
      <rect x="1" y="1" width="30" height="30" rx="7" fill="#1E242C"/>
      <polygon points="15,11.8 24,17 15,22.2 6,17" fill="#9CC4FA"/>
      <polygon points="6,17 15,22.2 15,30.2 6,25" fill="#5890E8"/>
      <polygon points="24,17 15,22.2 15,30.2 24,25" fill="#3464B4"/>
    </svg>`;
    const brand = `<a class="brand" href="index.html">${logo}MaxHub</a>`;
    const tabs = [
      ['index.html', '工具市场'],
      ['publish.html', '发布工具'],
    ];
    let adminTab = '';
    if (window.Api.hasRole(roles, 'admin') || window.Api.hasRole(roles, 'reviewer')) {
      adminTab = `<a class="tab ${page === 'admin.html' ? 'active' : ''}" href="admin.html">后台管理</a>`;
    }
    const tabHtml = tabs.map(([href, label]) =>
      `<a class="tab ${page === href ? 'active' : ''}" href="${href}">${label}</a>`).join('');

    const right = user
      ? `<span class="who" title="${user.username}">${user.username}</span><button class="btn-outline" onclick="window.Api.logout()">退出</button>`
      : `<button class="btn-primary" id="nav-login">登录</button>`;

    nav.innerHTML = brand + tabHtml + adminTab + `<span class="spacer"></span>` + right;
    const loginBtn = nav.querySelector('#nav-login');
    if (loginBtn) loginBtn.addEventListener('click', () => window.Api.startLogin());
  }

  window.addEventListener('DOMContentLoaded', async () => {
    // 处理飞书回调（若有）：登录成功后统一跳转到工具市场
    const handled = await window.Api.handleCallback();
    if (handled) {
      location.href = 'index.html';
      return;
    }
    let user = null;
    if (window.Api.isLoggedIn()) {
      try {
        user = await window.Api.me();
      } catch (e) {
        if (e instanceof window.Api.UnauthorizedError) user = null;
      }
    }
    renderNav(user);
    // 暴露给页面并广播就绪事件（页面用事件代替 setTimeout 轮询）
    window.MaxHubAuth = { user, roles: user?.roles ?? [] };
    window.dispatchEvent(new CustomEvent('maxhub-auth-ready'));
  });
})();
