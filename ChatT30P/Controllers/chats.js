angular.module('stats').controller('ChatsController', ["$scope", function ($scope) {
    $scope.isPaid = !!(window.UserVars && window.UserVars.IsPaid);
    if (!$scope.isPaid && window.$) {
        $("#modal-no-subscription").modal();
    }
}]);
