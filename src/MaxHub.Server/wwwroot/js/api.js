// MaxHub Web Portal 共享 API 封装与登录态
window.Api = (() => {
  const tokenKey = 'maxhubToken';
  const refreshKey = 'maxhubRefresh';
  const userKey = 'maxhubUser';

  // 用 refresh token 换新会话；并发 401 只触发一次刷新（共享同一 Promise）
  let refreshing = null;
  async function refreshSession() {
    const refreshToken = localStorage.getItem(refreshKey);
    if (!refreshToken) return false;
    refreshing ??= (async () => {
      try {
        const res = await fetch('/api/v1/auth/sessions/refresh', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ refreshToken }),
        });
        if (!res.ok) return false;
        const s = await res.json();
        localStorage.setItem(tokenKey, s.accessToken);
        localStorage.setItem(refreshKey, s.refreshToken);
        return true;
      } catch {
        return false;
      } finally {
        refreshing = null;
      }
    })();
    return refreshing;
  }

  async function api(path, opts = {}) {
    const headers = { ...(opts.body instanceof FormData ? {} : { 'Content-Type': 'application/json' }), ...opts.headers };
    const token = localStorage.getItem(tokenKey);
    if (token) headers.Authorization = 'Bearer ' + token;
    const res = await fetch(path, { ...opts, headers });
    if (res.status === 401 && opts.retry !== false) {
      // access token 过期（30 分钟）：先用 refresh token 透明续期并重试原请求
      if (path !== '/api/v1/auth/sessions/refresh' && await refreshSession()) {
        return api(path, { ...opts, retry: false });
      }
      // refresh 也失效：清理本地态并抛特殊错误，由页面决定是否提示重新登录
      localStorage.removeItem(tokenKey);
      localStorage.removeItem(refreshKey);
      localStorage.removeItem(userKey);
      throw new UnauthorizedError();
    }
    return res;
  }

  class UnauthorizedError extends Error {}

  function toast(text, ok = true) {
    let el = document.getElementById('msg');
    if (!el) {
      el = document.createElement('div');
      el.id = 'msg';
      document.body.appendChild(el);
    }
    el.textContent = text;
    el.style.display = 'block';
    el.style.background = ok ? '#1B3B2A' : '#42272A';
    el.style.color = ok ? 'var(--ok)' : 'var(--danger)';
    clearTimeout(el._t);
    el._t = setTimeout(() => el.style.display = 'none', 3000);
  }

  async function me() {
    const res = await api('/api/v1/auth/me');
    if (res.status !== 200) return null;
    return res.json();
  }

  function hasRole(roles, role) { return Array.isArray(roles) && roles.includes(role); }

  async function startLogin() {
    // 记录回跳地址（含锚点）：飞书卡片「去处理/查看进展」未登录时先登录，成功后回到原页面反馈区
    try { sessionStorage.setItem('maxhubReturnUrl', location.href); } catch { /* ignore */ }
    const res = await api('/api/v1/auth/feishu/qr-sessions?client=web', { method: 'POST', retry: false });
    const s = await res.json();
    if (s.authorizeUrl && s.authorizeUrl.startsWith('https://')) {
      location.href = s.authorizeUrl;
    } else {
      toast('服务端为 mock 模式，无法跳转飞书；请用测试端点授权', false);
    }
  }

  // 飞书回调落回本页：?code=..&state=<sessionId>
  async function handleCallback() {
    const params = new URLSearchParams(location.search);
    const code = params.get('code');
    const state = params.get('state');
    if (!code || !state) return false;
    const done = await api(`/api/v1/auth/feishu/qr-sessions/${state}/complete`, {
      method: 'POST', body: JSON.stringify({ code, state, client: 'web' }), retry: false,
    });
    if (!done.ok) { toast('授权码交换失败', false); return false; }
    const polled = await (await api(`/api/v1/auth/feishu/qr-sessions/${state}`, { retry: false })).json();
    if (polled.status === 'authorized' && polled.session) {
      localStorage.setItem(tokenKey, polled.session.accessToken);
      localStorage.setItem(refreshKey, polled.session.refreshToken);
      localStorage.setItem(userKey, polled.session.user.username);
      // 有回跳地址则回原页面（含锚点），否则由调用方决定去向
      let returnUrl = null;
      try {
        returnUrl = sessionStorage.getItem('maxhubReturnUrl');
        sessionStorage.removeItem('maxhubReturnUrl');
      } catch { /* ignore */ }
      history.replaceState(null, '', location.pathname);
      if (returnUrl && returnUrl !== location.href) {
        location.href = returnUrl;
      }
      return true;
    }
    toast('登录会话已过期，请重试', false);
    return false;
  }

  async function logout() {
    try { await api('/api/v1/auth/sessions/current', { method: 'DELETE', retry: false }); } catch { /* ignore */ }
    localStorage.removeItem(tokenKey);
    localStorage.removeItem(refreshKey);
    localStorage.removeItem(userKey);
    location.href = 'index.html';
  }

  function currentUser() { return localStorage.getItem(userKey); }
  function isLoggedIn() { return !!localStorage.getItem(tokenKey); }

  return { api, me, hasRole, startLogin, handleCallback, logout, toast, currentUser, isLoggedIn, UnauthorizedError };
})();
