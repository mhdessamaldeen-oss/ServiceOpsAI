/**
 * SupportFlow AI - Localization & UI Helper
 * 2026 Version
 */

function changeLanguage(lang) {
    const langInput = document.getElementById('lang-input');
    const langForm = document.getElementById('selectLanguage');
    
    if (langInput && langForm) {
        langInput.value = lang;
        langForm.submit();
    }
}

// Auto-align dropdowns for RTL if needed after initial load
document.addEventListener('DOMContentLoaded', function() {
    const isRtl = document.documentElement.dir === 'rtl';
    if (isRtl) {
        // Find all dropdown menus and ensure they align correctly
        const dropdowns = document.querySelectorAll('.dropdown-menu-end');
        dropdowns.forEach(dd => {
            dd.classList.remove('dropdown-menu-end');
            dd.classList.add('dropdown-menu-start'); // In RTL, Start is LEFT (correct for dropdowns on the right)
        });
    }
});
