// MaxHub Web Portal 导航渲染与角色感知
// 页面在 body 顶部放 <nav class="nav" id="topnav"></nav>，加载本脚本自动填充。
(function () {
  function renderNav(user) {
    const nav = document.getElementById('topnav');
    if (!nav) return;
    const page = location.pathname.split('/').pop() || 'index.html';
    const roles = user?.roles ?? [];

    const brand = `<a class="brand" href="index.html"><span class="dot">⬤</span>MaxHub</a>`;
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
      ? `<span class="who">${user.username}</span><button class="btn-outline" onclick="window.Api.logout()">退出</button>`
      : `<button class="btn-primary" id="nav-login">登录</button>`;

    nav.innerHTML = brand + tabHtml + adminTab + `<span class="spacer"></span>` + right;
    const loginBtn = nav.querySelector('#nav-login');
    if (loginBtn) loginBtn.addEventListener('click', () => window.Api.startLogin());
  }

  window.addEventListener('DOMContentLoaded', async () => {
    // 处理飞书回调（若有）
    const handled = await window.Api.handleCallback();
    let user = null;
    if (window.Api.isLoggedIn() && !handled) {
      try {
        user = await window.Api.me();
      } catch (e) {
        if (e instanceof window.Api.UnauthorizedError) user = null;
      }
    }
    renderNav(user);
    // 暴露给页面（如 publish 页判断是否已登录）
    window.MaxHubAuth = { user, roles: user?.roles ?? [] };
  });
})();
