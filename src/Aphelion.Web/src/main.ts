import './app.css';
import { mount } from 'svelte';
import App from './App.svelte';

// The context menu is replaced by nothing on purpose (WebView2 default menus are disabled too);
// text fields keep their native behaviour.
document.addEventListener('contextmenu', (e) => {
  const target = e.target as HTMLElement;
  if (!target.closest('input, textarea, .selectable')) e.preventDefault();
});

// Dropping a file anywhere must never navigate the page away.
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => e.preventDefault());

export default mount(App, { target: document.getElementById('app')! });
